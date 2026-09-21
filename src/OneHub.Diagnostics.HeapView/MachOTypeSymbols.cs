using System;
using System.Collections.Generic;
using System.IO;
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
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt32() != 0xfeedfacf)
            throw new InvalidDataException("Expected a thin, little-endian 64-bit Mach-O image or dSYM.");

        var result = new MachOTypeSymbols { CpuType = reader.ReadUInt32() };
        reader.ReadUInt32(); // CPU subtype
        uint fileType = reader.ReadUInt32();
        if (fileType != 2 && fileType != 6 && fileType != 10)
            throw new InvalidDataException("Symbols must come from a linked executable, dylib, or dSYM, not an object file.");

        uint commandCount = reader.ReadUInt32();
        uint commandBytes = reader.ReadUInt32();
        reader.ReadUInt32(); // flags
        reader.ReadUInt32(); // reserved
        long commandsEnd = 32L + commandBytes;
        CheckRange(stream, 32, commandBytes);
        if (commandCount > commandBytes / 8)
            throw new InvalidDataException("Invalid Mach-O load command count.");

        ulong? imageBase = null;
        uint symbolOffset = 0, symbolCount = 0, stringOffset = 0, stringSize = 0;
        for (uint i = 0; i < commandCount; i++)
        {
            long start = stream.Position;
            if (start + 8 > commandsEnd)
                throw new InvalidDataException("Truncated Mach-O load command.");
            uint command = reader.ReadUInt32();
            uint size = reader.ReadUInt32();
            if (size < 8 || start + size > commandsEnd)
                throw new InvalidDataException("Invalid Mach-O load command size.");

            switch (command)
            {
                case 0x19 when size >= 72: // LC_SEGMENT_64
                    string segment = Encoding.ASCII.GetString(reader.ReadBytes(16)).TrimEnd('\0');
                    ulong address = reader.ReadUInt64();
                    if (segment == "__TEXT")
                        imageBase = address;
                    break;
                case 0x1b when size >= 24: // LC_UUID
                    result.Uuid = reader.ReadBytes(16);
                    break;
                case 0x2 when size >= 24: // LC_SYMTAB
                    symbolOffset = reader.ReadUInt32();
                    symbolCount = reader.ReadUInt32();
                    stringOffset = reader.ReadUInt32();
                    stringSize = reader.ReadUInt32();
                    break;
            }
            stream.Position = start + size;
        }

        if (!imageBase.HasValue)
            throw new InvalidDataException("Mach-O image has no __TEXT segment.");
        if (!readSymbols || symbolCount == 0)
            return result;

        CheckRange(stream, symbolOffset, (long)symbolCount * 16);
        CheckRange(stream, stringOffset, stringSize);
        if (stringSize > int.MaxValue)
            throw new InvalidDataException("Mach-O string table is too large.");
        stream.Position = stringOffset;
        byte[] strings = new byte[(int)stringSize];
        stream.ReadExactly(strings);
        stream.Position = symbolOffset;
        for (uint i = 0; i < symbolCount; i++)
        {
            uint nameOffset = reader.ReadUInt32();
            byte type = reader.ReadByte();
            reader.ReadByte(); // section
            reader.ReadUInt16(); // description
            ulong address = reader.ReadUInt64();
            // Only defined section symbols; exclude STABS debug-map records.
            if ((type & 0xe0) != 0 || (type & 0x0e) != 0x0e ||
                address < imageBase.Value || address - imageBase.Value > uint.MaxValue)
                continue;
            if (nameOffset >= strings.Length)
                throw new InvalidDataException("Invalid Mach-O symbol name offset.");
            ReadOnlySpan<byte> name = strings.AsSpan((int)nameOffset);
            if (!name.StartsWith("__ZTV"u8) && !name.StartsWith("___GCStaticEEType_"u8))
                continue;
            int end = name.IndexOf((byte)0);
            if (end < 0)
                throw new InvalidDataException("Unterminated Mach-O symbol name.");
            string? typeName = DecodeTypeName(name[..end]);
            if (typeName != null)
                result.Types.TryAdd((uint)(address - imageBase.Value), typeName);
        }
        return result;
    }

    private static string? DecodeTypeName(ReadOnlySpan<byte> symbol)
    {
        // Shared synthetic method tables describe GC-static storage layouts, not
        // individual declaring types. Keep their native name rather than inventing one.
        if (symbol.StartsWith("___GCStaticEEType_"u8))
            return Encoding.UTF8.GetString(symbol[1..]);
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
        return length > 0 && length == symbol.Length - offset
            ? Encoding.UTF8.GetString(symbol[offset..]) : null;
    }

    private static void CheckRange(Stream stream, long offset, long size)
    {
        if (offset < 0 || size < 0 || offset > stream.Length || size > stream.Length - offset)
            throw new InvalidDataException("Mach-O data extends beyond the end of the file.");
    }
}
