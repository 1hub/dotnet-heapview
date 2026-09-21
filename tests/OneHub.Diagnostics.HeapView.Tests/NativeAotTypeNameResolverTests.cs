using LibObjectFile.IO;
using LibObjectFile.MachO;
using Graphs;
using OneHub.Diagnostics.HeapView;
using Xunit;

public sealed class NativeAotTypeNameResolverTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "heapview-symbol-tests-" + Guid.NewGuid());
    private readonly byte[] uuid = Guid.NewGuid().ToByteArray();

    public NativeAotTypeNameResolverTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public void UnsupportedAutomaticImageLeavesDumpReadable()
    {
        string image = Path.Combine(directory, "app");
        File.WriteAllText(image, "not a Mach-O image");
        var graph = new MemoryGraph(1);
        var type = graph.CreateType(0x1234, new Module(0) { Path = image });
        Assert.Equal(0, NativeAotTypeNameResolver.Resolve(graph));
        Assert.Equal("TypeID(0x1234)", Name(graph, type));
    }

    [Fact]
    public void ExplicitSymbolsAreNotReusedForAnotherModule()
    {
        string image = WriteImage("app", uuid, (0x1234, "__ZTV6String"));
        var graph = new MemoryGraph(1);
        graph.CreateType(0x1234, new Module(0) { Path = image });
        graph.CreateType(0x1234, new Module(0x1000) { Path = "/another/app" });
        Assert.Throws<InvalidOperationException>(() => NativeAotTypeNameResolver.Resolve(graph, image));
        Assert.Null(graph.ResolveTypeName);
    }

    [Fact]
    public void ResolvesSyntheticGcStaticStorageWithoutInventingADeclaringType()
    {
        string image = WriteImage("app", uuid, (0x1234, "___GCStaticEEType_010"));
        var graph = new MemoryGraph(1);
        var type = graph.CreateType(0x1234, new Module(0) { Path = image });
        Assert.Equal(1, NativeAotTypeNameResolver.Resolve(graph));
        Assert.Equal("__GCStaticEEType_010", Name(graph, type));
    }

    [Fact]
    public void ResolvesRvasIndependentOfAslrAndPreservesSuffixesAndNamedTypes()
    {
        string image = WriteImage("app", uuid, (0x1234, "__ZTV6String"),
            (0xf0000000, "__ZTV30S_P_CoreLib_System_RuntimeType"));
        var graph = new MemoryGraph(1);
        var module = new Module(0x71_00000000) { Path = image };
        var type = graph.CreateType(0x1234, module, typeNameSuffix: " (Bytes > 1K)");
        var high = graph.CreateType(unchecked((int)0xf0000000), module);
        var named = graph.CreateType("Already.Named");

        Assert.Equal(2, NativeAotTypeNameResolver.Resolve(graph));
        Assert.Equal("String (Bytes > 1K)", Name(graph, type));
        Assert.Equal("S_P_CoreLib_System_RuntimeType", Name(graph, high));
        Assert.Equal("Already.Named", Name(graph, named));
        Assert.Null(graph.ResolveTypeName);
    }

    [Fact]
    public void RequiresExactAddressAndIgnoresOtherSymbols()
    {
        string image = WriteImage("app", uuid, (0x1234, "__ZTV6String"),
            (0x2000, "__ZTV999Invalid"), (0x3000, "_SomeMethod"));
        var graph = new MemoryGraph(1);
        var module = new Module(0) { Path = image };
        var near = graph.CreateType(0x1235, module);
        var invalid = graph.CreateType(0x2000, module);
        var method = graph.CreateType(0x3000, module);
        Assert.Equal(0, NativeAotTypeNameResolver.Resolve(graph));
        Assert.Equal("TypeID(0x1235)", Name(graph, near));
        Assert.Equal("TypeID(0x2000)", Name(graph, invalid));
        Assert.Equal("TypeID(0x3000)", Name(graph, method));
    }

    [Theory]
    [InlineData("__ZTV7Type_Č", "Type_Č", 1)]
    [InlineData("__ZTV6Type_Č", "TypeID(0x1234)", 0)]
    public void TypeNameLengthCountsUtf8Bytes(string symbol, string expectedName, int resolved)
    {
        string image = WriteImage("app", uuid, (0x1234, symbol));
        var graph = new MemoryGraph(1);
        var type = graph.CreateType(0x1234, new Module(0) { Path = image });
        Assert.Equal(resolved, NativeAotTypeNameResolver.Resolve(graph));
        Assert.Equal(expectedName, Name(graph, type));
    }

    [Fact]
    public void MissingModulesKeepTypeIdsWithSuffixes()
    {
        var graph = new MemoryGraph(1);
        var missing = graph.CreateType(123, new Module(0) { Path = Path.Combine(directory, "absent") }, typeNameSuffix: " (static var)");
        var unknown = graph.CreateType(456, null!);
        Assert.Equal(0, NativeAotTypeNameResolver.Resolve(graph));
        Assert.Equal("TypeID(0x7b) (static var)", Name(graph, missing));
        Assert.Equal("TypeID(0x1c8)", Name(graph, unknown));
    }

    [Fact]
    public void FindsDsymAndRecoversExistingPathWithTrailingGarbage()
    {
        string image = WriteImage("app", uuid);
        string dsym = Path.Combine("app.dSYM", "Contents", "Resources", "DWARF", "app");
        WriteImage(dsym, uuid, (0x1234, "__ZTV6String"));
        var graph = new MemoryGraph(1);
        var type = graph.CreateType(0x1234, new Module(0) { Path = image + "歳\u0001" });
        Assert.Equal(1, NativeAotTypeNameResolver.Resolve(graph));
        Assert.Equal("String", Name(graph, type));
    }

    [Fact]
    public void AcceptsExplicitDsymAndReportsWhenImageIdentityCannotBeChecked()
    {
        WriteImage(Path.Combine("app.dSYM", "Contents", "Resources", "DWARF", "app"), uuid, (0x1234, "__ZTV6String"));
        var graph = new MemoryGraph(1);
        var type = graph.CreateType(0x1234, new Module(0) { Path = "/unavailable/app" });
        var log = new StringWriter();
        Assert.Equal(1, NativeAotTypeNameResolver.Resolve(graph, Path.Combine(directory, "app.dSYM"), log));
        Assert.Equal("String", Name(graph, type));
        Assert.Contains("identity cannot be verified", log.ToString());
    }

    [Fact]
    public void RejectsMismatchedExplicitSymbolsAndRestoresCallback()
    {
        string image = WriteImage("app", uuid);
        string wrong = WriteImage("wrong", Guid.NewGuid().ToByteArray(), (0x1234, "__ZTV6String"));
        var graph = new MemoryGraph(1);
        graph.CreateType(0x1234, new Module(0) { Path = image });
        Assert.Throws<InvalidDataException>(() => NativeAotTypeNameResolver.Resolve(graph, wrong));
        Assert.Null(graph.ResolveTypeName);
    }

    [Fact]
    public void MismatchedAutomaticDsymDoesNotResolveWrongNames()
    {
        string image = WriteImage("app", uuid);
        WriteImage(Path.Combine("app.dSYM", "Contents", "Resources", "DWARF", "app"), Guid.NewGuid().ToByteArray(), (0x1234, "__ZTV6String"));
        var graph = new MemoryGraph(1);
        var type = graph.CreateType(0x1234, new Module(0) { Path = image });
        Assert.Equal(0, NativeAotTypeNameResolver.Resolve(graph));
        Assert.Equal("TypeID(0x1234)", Name(graph, type));
    }

    [Fact]
    public void RejectsTruncatedSymbolTable()
    {
        string image = WriteImage("app", uuid, (0x1234, "__ZTV6String"));
        long truncatedLength;
        using (var input = File.OpenRead(image))
        {
            var machO = MachOFile.Read(input);
            var symbols = machO.LoadCommands.OfType<MachOSymbolTableCommand>().Single();
            truncatedLength = symbols.SymbolOffset + MachOSymbolTableCommand.GetSymbolSize(machO.Is64Bit) - 1;
        }
        using (var file = File.OpenWrite(image))
            file.SetLength(truncatedLength);
        var graph = new MemoryGraph(1);
        graph.CreateType(0x1234, new Module(0));
        Assert.Throws<InvalidDataException>(() => NativeAotTypeNameResolver.Resolve(graph, image));
    }

    [Fact]
    public void PreservesExistingResolverForUnmatchedTypes()
    {
        var graph = new MemoryGraph(1);
        var type = graph.CreateType(0x1234, new Module(0));
        Func<int, Module, string> existing = (_, _) => "Existing.Name";
        graph.ResolveTypeName = existing;
        NativeAotTypeNameResolver.Resolve(graph);
        Assert.Equal("Existing.Name", Name(graph, type));
        Assert.Same(existing, graph.ResolveTypeName);
    }

    private static string Name(MemoryGraph graph, NodeTypeIndex type) => graph.GetType(type, graph.AllocTypeNodeStorage()).Name;

    private string WriteImage(string name, byte[] identity, params (uint Rva, string Name)[] symbols)
    {
        string path = Path.Combine(directory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var image = new MachOFile
        {
            Is64Bit = true,
            CpuType = MachOCpuType.Arm64,
            FileType = name.Contains(".dSYM", StringComparison.Ordinal) ? MachOFileType.Dsym : MachOFileType.Execute
        };
        var text = new MachOSegment
        {
            Type = MachOLoadCommandType.Segment64,
            Is64Bit = true,
            Name = "__TEXT",
            VmAddress = 0x100000000,
            VmSize = 0x100000000
        };
        // Reserve address space for the synthetic type tables, including high RVAs,
        // without allocating their contents in the test file.
        text.Sections.Add(new MachOSection
        {
            Name = "__types", SegmentName = text.Name, Address = text.VmAddress,
            Size = text.VmSize, SectionType = MachOSectionType.ZeroFill
        });
        text.Size = MachOSegment.ComputeCommandSize(image.Is64Bit, text.Sections.Count);
        var symtab = new MachOSymbolTableCommand
        {
            Type = MachOLoadCommandType.SymbolTable,
            Size = MachOSymbolTableCommand.CommandSize,
            SymbolCount = (uint)symbols.Length
        };
        image.LoadCommands.Add(text);
        image.LoadCommands.Add(new MachOUuidCommand
        {
            Type = MachOLoadCommandType.Uuid, Size = MachOUuidCommand.CommandSize,
            Uuid = new Guid(identity, bigEndian: true)
        });
        image.LoadCommands.Add(symtab);
        image.Content.Add(new MachOHeaderContent { Size = image.HeaderSize });
        image.Content.Add(new MachOLoadCommandTable { Position = image.HeaderSize, Size = image.SizeOfCommands });

        using var strings = new MemoryStream();
        strings.WriteByte(0);
        var entries = new List<MachOSymbol>();
        foreach (var symbol in symbols)
        {
            entries.Add(new MachOSymbol
            {
                Name = symbol.Name, NameOffset = (uint)strings.Position,
                RawType = (byte)MachOSymbolKind.Section, SectionIndex = 1,
                Value = text.VmAddress + symbol.Rva
            });
            strings.WriteStringUTF8NullTerminated(symbol.Name);
        }
        var table = new SymbolTableContent(entries) { Position = image.LoadCommandsEndOffset };
        symtab.SymbolOffset = (uint)table.Position;
        symtab.StringOffset = (uint)(table.Position + table.Size);
        symtab.StringSize = (uint)strings.Length;
        image.Content.Add(table);
        image.Content.Add(new MachOStreamContent(strings) { Position = symtab.StringOffset });
        text.FileSize = image.ComputeFileSize();

        using var output = File.Create(path);
        image.Write(output);
        return path;
    }

    // LibObjectFile 2.3.1 models symbol entries for reading, but accepts writable
    // symbol tables as raw MachOContent. Keep only this nlist_64 encoder here;
    // the library generates and validates the Mach-O header and load commands.
    private sealed class SymbolTableContent : MachOContent
    {
        private readonly IReadOnlyList<MachOSymbol> symbols;

        public SymbolTableContent(IReadOnlyList<MachOSymbol> symbols)
        {
            this.symbols = symbols;
            Size = (ulong)symbols.Count * MachOSymbolTableCommand.GetSymbolSize(is64Bit: true);
        }

        public override void WriteContent(MachOWriter writer)
        {
            foreach (var symbol in symbols)
            {
                writer.WriteU32(symbol.NameOffset);
                writer.WriteU8(symbol.RawType);
                writer.WriteU8(symbol.SectionIndex);
                writer.WriteU16(symbol.Description);
                writer.WriteU64(symbol.Value);
            }
        }
    }
}
