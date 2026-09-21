using Graphs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OneHub.Diagnostics.HeapView;

/// <summary>Resolves deferred NativeAOT type names from local macOS native symbols.</summary>
public static class NativeAotTypeNameResolver
{
    /// <summary>
    /// Resolve names before constructing a snapshot (which reads and caches type names).
    /// An explicit symbol file must belong to the dump's single native module. It can be
    /// an unstripped Mach-O image, a dSYM bundle, or its Contents/Resources/DWARF file.
    /// Without an explicit file, look beside the recorded image for its dSYM, then in
    /// the image itself. Missing automatic symbols leave TypeID placeholders intact.
    /// </summary>
    public static int Resolve(MemoryGraph graph, string? symbolFilePath = null, TextWriter? log = null)
    {
        if (!graph.HasDeferedTypeNames)
            return 0;
        log ??= TextWriter.Null;

        var previousResolver = graph.ResolveTypeName;
        MachOTypeSymbols? explicitSymbols = symbolFilePath == null ? null : MachOTypeSymbols.Read(GetSymbolFile(symbolFilePath));
        var symbolsByModule = new Dictionary<Module, MachOTypeSymbols?>();
        int resolved = 0;
        Module? explicitModule = null;
        try
        {
            graph.ResolveTypeName = (id, module) =>
            {
                if (module != null && !symbolsByModule.ContainsKey(module))
                {
                    string? imagePath = FindImage(module.Path, log);
                    MachOTypeSymbols? symbols = null;
                    if (explicitSymbols != null)
                    {
                        if (explicitModule != null && explicitModule != module)
                            throw new InvalidOperationException("An explicit symbol file requires a dump with a single native module.");
                        explicitModule = module;
                        symbols = explicitSymbols;
                        if (imagePath != null)
                            ValidateIdentity(MachOTypeSymbols.Read(imagePath, readSymbols: false), symbols);
                        else
                            log.WriteLine("The recorded image is unavailable; symbol identity cannot be verified. Use symbols from the exact build that produced the dump.");
                    }
                    else if (imagePath != null)
                    {
                        try
                        {
                            string? dsym = FindDsym(imagePath);
                            symbols = MachOTypeSymbols.Read(dsym ?? imagePath);
                            if (dsym != null)
                                ValidateIdentity(MachOTypeSymbols.Read(imagePath, readSymbols: false), symbols);
                        }
                        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                        {
                            symbols = null;
                            log.WriteLine($"Native symbols unavailable for {imagePath}: {ex.Message}");
                        }
                    }
                    symbolsByModule[module] = symbols;
                }

                if (module != null && symbolsByModule[module]?.Types.TryGetValue(unchecked((uint)id), out string? name) == true)
                {
                    resolved++;
                    return name;
                }
                // Always return a nonempty fallback: Graph appends size/static suffixes
                // even when a resolver returns null, which otherwise loses the TypeID.
                string? fallback = previousResolver?.Invoke(id, module!);
                return !string.IsNullOrEmpty(fallback) ? fallback : $"TypeID(0x{unchecked((uint)id):x})";
            };
            var storage = graph.AllocTypeNodeStorage();
            for (NodeTypeIndex i = 0; i < graph.NodeTypeIndexLimit; i++)
                _ = graph.GetType(i, storage).Name;
        }
        finally
        {
            graph.ResolveTypeName = previousResolver;
        }
        log.WriteLine($"Resolved {resolved:N0} deferred type names from native symbols.");
        return resolved;
    }

    private static void ValidateIdentity(MachOTypeSymbols image, MachOTypeSymbols symbols)
    {
        if (image.CpuType != symbols.CpuType || image.Uuid == null || symbols.Uuid == null ||
            !image.Uuid.AsSpan().SequenceEqual(symbols.Uuid))
            throw new InvalidDataException("The native symbol file does not match the recorded executable's Mach-O UUID and architecture.");
    }

    private static string GetSymbolFile(string path)
    {
        path = Path.GetFullPath(path);
        if (!Directory.Exists(path))
            return path;
        string dwarf = Path.Combine(path, "Contents", "Resources", "DWARF");
        string[] files = Directory.Exists(dwarf) ? Directory.GetFiles(dwarf) : Array.Empty<string>();
        if (files.Length != 1)
            throw new ArgumentException("Select a dSYM containing one DWARF image, or specify its DWARF image file directly.", nameof(path));
        return files[0];
    }

    private static string? FindDsym(string imagePath)
    {
        string name = Path.GetFileName(imagePath);
        string adjacent = Path.Combine(imagePath + ".dSYM", "Contents", "Resources", "DWARF", name);
        if (File.Exists(adjacent))
            return adjacent;
        // Apple app builds normally place Foo.app.dSYM beside Foo.app.
        var macos = new DirectoryInfo(Path.GetDirectoryName(imagePath)!);
        if (macos.Name == "MacOS" && macos.Parent?.Name == "Contents" && macos.Parent.Parent is { } app)
        {
            string appDsym = Path.Combine(app.FullName + ".dSYM", "Contents", "Resources", "DWARF", name);
            if (File.Exists(appDsym))
                return appDsym;
        }
        return null;
    }

    private static string? FindImage(string? path, TextWriter log)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        if (File.Exists(path))
            return path;
        // Some NativeAOT module events have trailing garbage, including control
        // characters. Recover only an existing file prefix, never a guessed filename.
        if (path.Any(char.IsControl))
        {
            int separator = path.LastIndexOf(Path.DirectorySeparatorChar);
            for (int length = path.Length - 1; length > separator + 1; length--)
            {
                string prefix = path[..length];
                if (File.Exists(prefix))
                {
                    log.WriteLine($"Ignoring trailing garbage in the recorded module path; using {prefix}.");
                    return prefix;
                }
            }
        }
        return null;
    }
}
