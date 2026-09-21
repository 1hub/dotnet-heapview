using System.Text;
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
        using (var file = File.OpenWrite(image))
            file.SetLength(155);
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

    // Minimal linked Mach-O fixture, with real header/load-command/nlist layouts.
    private string WriteImage(string name, byte[] identity, params (uint Rva, string Name)[] symbols)
    {
        string path = Path.Combine(directory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        const uint symbolOffset = 32 + 72 + 24 + 24;
        byte[] strings = Encoding.UTF8.GetBytes("\0" + string.Join('\0', symbols.Select(s => s.Name)) + "\0");
        writer.Write(0xfeedfacfU);
        writer.Write(0x100000cU); // arm64
        writer.Write(0U);
        writer.Write(2U); // MH_EXECUTE
        writer.Write(3U);
        writer.Write(120U);
        writer.Write(0UL);
        writer.Write(0x19U);
        writer.Write(72U);
        writer.Write(Encoding.ASCII.GetBytes("__TEXT".PadRight(16, '\0')));
        writer.Write(0x100000000UL);
        writer.Write(new byte[40]);
        writer.Write(0x1bU);
        writer.Write(24U);
        writer.Write(identity);
        writer.Write(2U);
        writer.Write(24U);
        writer.Write(symbolOffset);
        writer.Write((uint)symbols.Length);
        writer.Write(symbolOffset + (uint)symbols.Length * 16);
        writer.Write((uint)strings.Length);
        uint offset = 1;
        foreach (var symbol in symbols)
        {
            writer.Write(offset);
            writer.Write((byte)0x0e); // N_SECT
            writer.Write((byte)1);
            writer.Write((ushort)0);
            writer.Write(0x100000000UL + symbol.Rva);
            offset += (uint)Encoding.UTF8.GetByteCount(symbol.Name) + 1;
        }
        writer.Write(strings);
        return path;
    }
}
