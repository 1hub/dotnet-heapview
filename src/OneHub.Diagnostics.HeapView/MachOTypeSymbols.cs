using LibObjectFile;
using LibObjectFile.MachO;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace OneHub.Diagnostics.HeapView;

// NativeAOT's UnixNodeMangler emits method tables as _ZTV<length><type name>.
// Mach-O adds one more leading underscore. Both unstripped images and dSYMs
// retain these symbols, so reading their symbol table needs no Apple tools.
internal sealed class MachOTypeSymbols
{
    public byte[]? Uuid { get; private set; }
    public uint CpuType { get; private set; }
    public Dictionary<uint, string> Types { get; } = new();

    public static MachOTypeSymbols Read(string path, bool readSymbols = true)
    {
        using var stream = File.OpenRead(path);
        try
        {
            // Keep sections (especially DWARF) as stream views; only the symbol table
            // is decoded. The input remains open until all required names are copied.
            var image = MachOFile.Read(stream, new MachOReaderOptions { UseSubStream = true });
            if (!image.Is64Bit)
                throw new InvalidDataException("Expected a 64-bit Mach-O image or dSYM.");
            if (image.FileType is not (MachOFileType.Execute or MachOFileType.Dylib or MachOFileType.Dsym))
                throw new InvalidDataException("Symbols must come from a linked executable, dylib, or dSYM, not an object file.");

            ulong imageBase = image.FindSegment("__TEXT")?.VmAddress
                ?? throw new InvalidDataException("Mach-O image has no __TEXT segment.");
            var result = new MachOTypeSymbols
            {
                CpuType = (uint)image.CpuType,
                Uuid = image.LoadCommands.OfType<MachOUuidCommand>().FirstOrDefault()?.Uuid.ToByteArray(bigEndian: true)
            };
            if (!readSymbols)
                return result;

            foreach (var symbol in image.ReadSymbolTable())
            {
                // Only defined section symbols; exclude STABS debug-map records.
                if (symbol.IsDebug || symbol.Kind != MachOSymbolKind.Section ||
                    symbol.Value < imageBase || symbol.Value - imageBase > uint.MaxValue)
                    continue;
                string? typeName = DecodeTypeName(symbol.Name);
                if (typeName != null)
                    result.Types.TryAdd((uint)(symbol.Value - imageBase), typeName);
            }
            return result;
        }
        catch (ObjectFileException ex)
        {
            // Keep the resolver's existing missing/malformed-symbol fallback contract.
            throw new InvalidDataException(ex.Message, ex);
        }
    }

    private static string? DecodeTypeName(string symbol)
    {
        // Shared synthetic method tables describe GC-static storage layouts, not
        // individual declaring types. Keep their native name rather than inventing one.
        if (symbol.StartsWith("___GCStaticEEType_", StringComparison.Ordinal))
            return symbol[1..];
        if (!symbol.StartsWith("__ZTV", StringComparison.Ordinal))
            return null;
        int offset = 5;
        int length = 0;
        while (offset < symbol.Length && symbol[offset] >= '0' && symbol[offset] <= '9')
        {
            int digit = symbol[offset++] - '0';
            if (length > (int.MaxValue - digit) / 10)
                return null;
            length = length * 10 + digit;
        }
        // Do not guess how underscores map to namespaces: mangling is lossy.
        return length > 0 && length == Encoding.UTF8.GetByteCount(symbol.AsSpan(offset))
            ? symbol[offset..] : null;
    }
}
