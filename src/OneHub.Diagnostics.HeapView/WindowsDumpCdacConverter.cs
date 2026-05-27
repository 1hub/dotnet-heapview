#if NET11_0_OR_GREATER
using Graphs;
using Microsoft.Diagnostics.DataContractReader;
using Microsoft.Diagnostics.DataContractReader.Contracts;
using Microsoft.Diagnostics.Runtime;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using GrowableNodeIndexArray = System.Collections.Generic.GrowableArray<Graphs.NodeIndex>;
using CdacModuleHandle = Microsoft.Diagnostics.DataContractReader.Contracts.ModuleHandle;

namespace OneHub.Diagnostics.HeapView;

public static class WindowsDumpCdacConverter
{
    private static readonly string[] ContractDescriptorExportNames =
    [
        "DotNetRuntimeContractDescriptor",
        "g_dataContractDescriptor",
    ];

    public static HeapSnapshot Convert(string fileName)
    {
        using DataTarget dataTarget = DataTarget.LoadDump(fileName);
        ModuleInfo runtimeModule = FindRuntimeModule(dataTarget);
        ulong descriptorAddress = FindContractDescriptorAddress(runtimeModule);

        if (dataTarget.DataReader is not IMemoryReader memoryReader)
            throw new InvalidOperationException("The dump reader does not expose a memory reader.");

        if (!ContractDescriptorTarget.TryCreate(
                descriptorAddress,
                ReadFromTarget,
                WriteToTarget,
                GetThreadContext,
                [CoreCLRContracts.Register],
                out ContractDescriptorTarget? target))
        {
            throw new InvalidOperationException($"Could not create a cDAC target from contract descriptor 0x{descriptorAddress:x}.");
        }

        MemoryGraph graph = BuildGraph(target);
        return new HeapSnapshot(graph);

        int ReadFromTarget(ulong address, Span<byte> buffer)
        {
            int read = memoryReader.Read(address, buffer);
            return read == buffer.Length ? 0 : -1;
        }

        int WriteToTarget(ulong address, Span<byte> buffer) => -1;

        int GetThreadContext(uint threadId, uint contextFlags, Span<byte> context)
            => dataTarget.DataReader.GetThreadContext(threadId, contextFlags, context) ? 0 : -1;

    }

    private static ModuleInfo FindRuntimeModule(DataTarget dataTarget)
    {
        return dataTarget.EnumerateModules()
            .FirstOrDefault(m =>
            {
                string fileName = Path.GetFileName(m.FileName);
                return fileName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase)
                    || fileName.Equals("libcoreclr.so", StringComparison.OrdinalIgnoreCase)
                    || fileName.Equals("libcoreclr.dylib", StringComparison.OrdinalIgnoreCase);
            })
            ?? throw new InvalidOperationException("Could not find the CoreCLR runtime module in the dump.");
    }

    private static ulong FindContractDescriptorAddress(ModuleInfo runtimeModule)
    {
        foreach (string exportName in ContractDescriptorExportNames)
        {
            ulong address = runtimeModule.GetExportSymbolAddress(exportName);
            if (address != 0)
                return address;
        }

        throw new InvalidOperationException(
            $"Could not find a cDAC contract descriptor export in {runtimeModule.FileName}.");
    }

    private static MemoryGraph BuildGraph(ContractDescriptorTarget target)
    {
        IGC gc = target.Contracts.GC;
        IObject objects = target.Contracts.Object;
        IRuntimeTypeSystem typeSystem = target.Contracts.RuntimeTypeSystem;
        TypeNameResolver typeNameResolver = new(target.Contracts.Loader, target.Contracts.EcmaMetadata, typeSystem);
        int pointerSize = target.PointerSize;

        MemoryGraph graph = new(10000, target.PointerSize == 8);
        NodeIndex rootIndex = graph.CreateNode();
        graph.RootIndex = rootIndex;
        NodeTypeIndex rootType = graph.CreateType("[root]", string.Empty, 0);
        NodeTypeIndex unknownType = graph.CreateType("[unknown]", string.Empty, pointerSize);

        List<HeapObject> heapObjects = EnumerateObjects(gc, objects, typeSystem, pointerSize).ToList();
        Dictionary<ulong, HeapObject> objectByAddress = heapObjects.ToDictionary(o => o.Address);
        Dictionary<ulong, NodeTypeIndex> typeIndexByMethodTable = new();

        foreach (HeapObject heapObject in heapObjects)
        {
            NodeIndex nodeIndex = graph.CreateNode();
            heapObject.NodeIndex = nodeIndex;
            graph.SetAddress(nodeIndex, heapObject.Address);

            NodeTypeIndex typeIndex = unknownType;
            if (heapObject.MethodTable != 0)
            {
                if (!typeIndexByMethodTable.TryGetValue(heapObject.MethodTable, out typeIndex))
                {
                    string typeName = heapObject.IsFree
                        ? "[free]"
                        : typeNameResolver.GetTypeName(heapObject.TypeHandle, (ulong)heapObject.MethodTable);
                    typeIndex = graph.CreateType(typeName, string.Empty, heapObject.PointerSize);
                    typeIndexByMethodTable.Add(heapObject.MethodTable, typeIndex);
                }
            }

            heapObject.TypeIndex = typeIndex;
        }

        GrowableNodeIndexArray rootChildren = new(heapObjects.Count);
        foreach (HeapObject heapObject in heapObjects)
            rootChildren.Add(heapObject.NodeIndex);
        graph.SetNode(rootIndex, rootType, 0, rootChildren);

        foreach (HeapObject heapObject in heapObjects)
        {
            GrowableNodeIndexArray children = new(4);
            foreach (ulong reference in EnumerateReferences(target, typeSystem, heapObject, objectByAddress))
                children.Add(objectByAddress[reference].NodeIndex);

            graph.SetNode(heapObject.NodeIndex, heapObject.TypeIndex, checked((int)Math.Min(heapObject.Size, int.MaxValue)), children);
        }

        graph.AllowReading();
        return graph;
    }

    private static IEnumerable<HeapObject> EnumerateObjects(
        IGC gc,
        IObject objects,
        IRuntimeTypeSystem typeSystem,
        int pointerSize)
    {
        foreach (GCHeapData heapData in EnumerateHeapData(gc))
        {
            foreach (GCHeapSegmentData segment in EnumerateHeapSegments(gc, heapData))
            {
                ulong current = segment.Mem;
                ulong end = Math.Max((ulong)segment.Allocated, (ulong)segment.Used);

                while (current != 0 && current < end)
                {
                    if (!TryGetObjectInfo(objects, typeSystem, current, pointerSize, out HeapObject heapObject))
                        break;
                    yield return heapObject;

                    ulong next = AlignUp(current + heapObject.Size, (ulong)pointerSize);
                    if (next <= current)
                        break;
                    current = next;
                }
            }
        }
    }

    private static IEnumerable<GCHeapData> EnumerateHeapData(IGC gc)
    {
        TargetPointer[] heaps = gc.GetGCHeaps().ToArray();
        if (heaps.Length == 0)
        {
            yield return gc.GetHeapData();
            yield break;
        }

        foreach (TargetPointer heap in heaps)
            yield return gc.GetHeapData(heap);
    }

    private static IEnumerable<GCHeapSegmentData> EnumerateHeapSegments(IGC gc, GCHeapData heapData)
    {
        HashSet<ulong> seen = new();

        foreach (GCGenerationData generation in heapData.GenerationTable)
        {
            TargetPointer segmentAddress = generation.StartSegment;
            while ((ulong)segmentAddress != 0 && seen.Add(segmentAddress))
            {
                GCHeapSegmentData segment = gc.GetHeapSegmentData(segmentAddress);
                yield return segment;
                segmentAddress = segment.Next;
            }
        }
    }

    private static bool TryGetObjectInfo(
        IObject objects,
        IRuntimeTypeSystem typeSystem,
        ulong address,
        int pointerSize,
        out HeapObject heapObject)
    {
        heapObject = new HeapObject { Address = address, PointerSize = pointerSize };

        TargetPointer methodTable;
        try
        {
            methodTable = objects.GetMethodTableAddress(address);
        }
        catch
        {
            return false;
        }

        if ((ulong)methodTable == 0)
            return false;

        TypeHandle typeHandle = typeSystem.GetTypeHandle(methodTable);
        uint baseSize = typeSystem.GetBaseSize(typeHandle);
        uint componentSize = typeSystem.GetComponentSize(typeHandle);
        ulong objectSize = baseSize;
        bool isArray = typeSystem.IsArray(typeHandle, out _);

        if (isArray)
        {
            objects.GetArrayData(address, out uint componentCount, out _, out _);
            objectSize += (ulong)componentSize * componentCount;
        }
        else if (typeSystem.IsString(typeHandle))
        {
            string value = objects.GetStringValue(address);
            objectSize += (ulong)value.Length * sizeof(char);
        }

        if (objectSize == 0)
            objectSize = (ulong)pointerSize;

        heapObject.MethodTable = methodTable;
        heapObject.TypeHandle = typeHandle;
        heapObject.Size = objectSize;
        heapObject.ContainsReferences = typeSystem.ContainsGCPointers(typeHandle);
        heapObject.IsFree = typeSystem.IsFreeObjectMethodTable(typeHandle);
        return true;
    }

    private static IEnumerable<ulong> EnumerateReferences(
        ContractDescriptorTarget target,
        IRuntimeTypeSystem typeSystem,
        HeapObject heapObject,
        Dictionary<ulong, HeapObject> objectByAddress)
    {
        if (!heapObject.ContainsReferences)
            yield break;

        for (ulong address = heapObject.Address + (ulong)heapObject.PointerSize;
             address + (ulong)heapObject.PointerSize <= heapObject.Address + heapObject.Size;
             address += (ulong)heapObject.PointerSize)
        {
            TargetPointer reference;
            try
            {
                reference = target.ReadPointer(address);
            }
            catch
            {
                continue;
            }

            ulong referenceAddress = reference;
            if (referenceAddress != heapObject.Address && objectByAddress.ContainsKey(referenceAddress))
                yield return referenceAddress;
        }
    }

    private static ulong AlignUp(ulong value, ulong alignment)
        => (value + alignment - 1) & ~(alignment - 1);

    private sealed class TypeNameResolver(
        ILoader loader,
        IEcmaMetadata ecmaMetadata,
        IRuntimeTypeSystem typeSystem)
    {
        private readonly Dictionary<TypeHandle, string> namesByTypeHandle = new();

        public string GetTypeName(TypeHandle typeHandle, ulong methodTable)
        {
            try
            {
                return GetTypeName(typeHandle);
            }
            catch
            {
                return $"MethodTable 0x{methodTable:x}";
            }
        }

        private string GetTypeName(TypeHandle typeHandle)
        {
            if (namesByTypeHandle.TryGetValue(typeHandle, out string? cachedName))
                return cachedName;

            string name = ResolveTypeName(typeHandle);
            namesByTypeHandle.Add(typeHandle, name);
            return name;
        }

        private string ResolveTypeName(TypeHandle typeHandle)
        {
            if (typeSystem.IsString(typeHandle))
                return "System.String";

            if (typeSystem.IsArray(typeHandle, out uint rank))
            {
                TypeHandle elementType = typeSystem.GetTypeParam(typeHandle);
                string suffix = rank <= 1 ? "[]" : $"[{new string(',', checked((int)rank - 1))}]";
                return GetTypeName(elementType) + suffix;
            }

            CorElementType primitiveType = typeSystem.GetSignatureCorElementType(typeHandle);
            if (TryGetPrimitiveName(primitiveType, out string? primitiveName))
                return primitiveName;

            uint typeDefToken = typeSystem.GetTypeDefToken(typeHandle);
            string metadataName = typeDefToken == 0
                ? $"MethodTable 0x{(ulong)typeSystem.GetCanonicalMethodTable(typeHandle):x}"
                : GetMetadataTypeName(typeHandle, typeDefToken);

            ReadOnlySpan<TypeHandle> instantiation = typeSystem.GetInstantiation(typeHandle);
            if (!instantiation.IsEmpty)
            {
                int tickIndex = metadataName.IndexOf('`', StringComparison.Ordinal);
                if (tickIndex >= 0)
                    metadataName = metadataName[..tickIndex];

                string[] genericArguments = instantiation.ToArray().Select(GetTypeName).ToArray();
                metadataName += $"<{string.Join(", ", genericArguments)}>";
            }

            return metadataName;
        }

        private string GetMetadataTypeName(TypeHandle typeHandle, uint typeDefToken)
        {
            TargetPointer modulePointer = typeSystem.GetModule(typeHandle);
            CdacModuleHandle moduleHandle = loader.GetModuleHandleFromModulePtr(modulePointer);
            MetadataReader reader = ecmaMetadata.GetMetadata(moduleHandle)
                ?? throw new InvalidOperationException($"No metadata reader for module 0x{(ulong)modulePointer:x}.");

            EntityHandle entityHandle = MetadataTokens.EntityHandle(unchecked((int)typeDefToken));
            if (entityHandle.Kind != HandleKind.TypeDefinition)
                return $"Token 0x{typeDefToken:x8}";

            return GetMetadataTypeName(reader, (TypeDefinitionHandle)entityHandle);
        }

        private static string GetMetadataTypeName(MetadataReader reader, TypeDefinitionHandle handle)
        {
            TypeDefinition definition = reader.GetTypeDefinition(handle);
            string name = reader.GetString(definition.Name);
            string ns = reader.GetString(definition.Namespace);
            TypeDefinitionHandle declaringType = definition.GetDeclaringType();

            if (!declaringType.IsNil)
                return $"{GetMetadataTypeName(reader, declaringType)}.{name}";

            return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
        }

        private static bool TryGetPrimitiveName(CorElementType type, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? name)
        {
            name = type switch
            {
                CorElementType.Boolean => "System.Boolean",
                CorElementType.Char => "System.Char",
                CorElementType.I1 => "System.SByte",
                CorElementType.U1 => "System.Byte",
                CorElementType.I2 => "System.Int16",
                CorElementType.U2 => "System.UInt16",
                CorElementType.I4 => "System.Int32",
                CorElementType.U4 => "System.UInt32",
                CorElementType.I8 => "System.Int64",
                CorElementType.U8 => "System.UInt64",
                CorElementType.R4 => "System.Single",
                CorElementType.R8 => "System.Double",
                CorElementType.I => "System.IntPtr",
                CorElementType.U => "System.UIntPtr",
                CorElementType.String => "System.String",
                CorElementType.Object => "System.Object",
                _ => null,
            };
            return name is not null;
        }
    }

    private sealed class HeapObject
    {
        public ulong Address { get; init; }
        public int PointerSize { get; init; }
        public ulong Size { get; set; }
        public TargetPointer MethodTable { get; set; }
        public TypeHandle TypeHandle { get; set; }
        public bool ContainsReferences { get; set; }
        public bool IsFree { get; set; }
        public NodeIndex NodeIndex { get; set; }
        public NodeTypeIndex TypeIndex { get; set; }
    }
}
#endif
