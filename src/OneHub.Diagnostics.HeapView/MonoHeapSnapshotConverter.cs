using Graphs;
using System;
using System.Buffers.Binary;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Runtime.CompilerServices;
using System.Diagnostics.CodeAnalysis;

namespace OneHub.Diagnostics.HeapView
{
    public class MonoHeapSnapshotConverter
    {
        public static MemoryGraph Convert(Stream inputStream)
        {
            MemoryGraph memoryGraph = new MemoryGraph(10000);
            var rootBuilder = new MemoryNodeBuilder(memoryGraph, "[MonoVM Roots]");

            using var counterChunk = new RiffChunk(inputStream);
            Assert(counterChunk.ChunkId.SequenceEqual("CNTR"u8));
            var counters = LoadCounters(counterChunk);

            int classCount = (int)counters["snapshot/num-classes"];
            int objectCount = (int)counters["snapshot/num-objects"];
            int totalRootCount = (int)counters["snapshot/num-roots"];
            int refCount = (int)counters["snapshot/num-refs"];

            Dictionary<ulong, string>? stringTable = null;
            var classes = new SnapshotClass[classCount];
            int classOffset = 0;
            var objects = new SnapshotObject[objectCount];
            int objectOffset = 0;
            Dictionary<uint, List<uint>> objectIdsToRefs = new();
            Dictionary<ulong, List<NodeIndex>> rootKindToRefs = new();
            while (inputStream.Position < inputStream.Length)
            {
                using var chunk = new RiffChunk(inputStream);
                if (chunk.ChunkId.SequenceEqual("STBL"u8))
                {
                    stringTable = DecodeStringTable(chunk);
                }
                else if (chunk.ChunkId.SequenceEqual("TYPE"u8))
                {
                    DecodeClasses(chunk, classes, ref classOffset);
                }
                else if (chunk.ChunkId.SequenceEqual("OBJH"u8))
                {
                    DecodeObjectHeaders(chunk, objects, ref objectOffset);
                }
                else if (chunk.ChunkId.SequenceEqual("REFS"u8))
                {
                    // Decode references
                    var data = chunk.Data;
                    while (data.Length > 0)
                    {
                        var objectId = BinaryPrimitives.ReadUInt32LittleEndian(data);
                        data = data.Slice(4);
                        Assert(TryReadULEBInt(ref data, out var count));

                        if (!objectIdsToRefs.TryGetValue(objectId, out var refList))
                        {
                            refList = new List<uint>((int)count);
                            objectIdsToRefs.Add(objectId, refList);
                        }
                        else
                        {
                            refList.Capacity = refList.Count + (int)count;
                        }

                        for (int i = 0; i < (int)count; i++)
                        {
                            refList.Add(BinaryPrimitives.ReadUInt32LittleEndian(data));
                            data = data.Slice(4);
                        }
                    }
                }
                else if (chunk.ChunkId.SequenceEqual("ROOT"u8))
                {
                    // Decode roots
                    var data = chunk.Data;
                    while (data.Length > 0)
                    {
                        Assert(TryReadULEBInt(ref data, out var kind));
                        Assert(TryReadULEBInt(ref data, out var rootCount));

                        if (!rootKindToRefs.TryGetValue(kind, out var refList))
                        {
                            refList = new List<NodeIndex>((int)rootCount);
                            rootKindToRefs.Add(kind, refList);
                        }
                        else
                        {
                            refList.Capacity = refList.Count + (int)rootCount;
                        }

                        for (uint i = 0; i < rootCount; i++)
                        {
                            var address = BinaryPrimitives.ReadUInt32LittleEndian(data);
                            var objectId = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4));
                            data = data.Slice(8);

                            NodeIndex nodeIndex = memoryGraph.GetNodeIndex(objectId);
                            memoryGraph.SetAddress(nodeIndex, address);

                            refList.Add(nodeIndex);
                        }
                    }
                }
            }

            Array.Sort(classes, 0, classOffset, SnapshotClass.AddressComparer.Instance);
            Array.Sort(objects, 0, objectOffset, SnapshotObject.AddressComparer.Instance);

            Assert(stringTable is not null);

            // Create classes in the memory graph
            for (int i = 0; i < classCount; i++)
            {
                ref var klass = ref classes[i];
                klass.TypeIndex = memoryGraph.CreateType(klass.GetFullName(classes, stringTable));
            }

            // Create instances in the memory graph
            var childrenHolder = new GrowableArray<NodeIndex>();
            for (int i = 0; i < objectCount; i++)
            {
                ref var obj = ref objects[i];
                NodeIndex nodeIndex = memoryGraph.GetNodeIndex(obj.Object);
                childrenHolder.Clear();

                if (objectIdsToRefs.TryGetValue(obj.Object, out var refList))
                {
                    foreach (var reference in refList)
                        childrenHolder.Add(memoryGraph.GetNodeIndex(reference));
                }

                memoryGraph.SetNode(nodeIndex, Class(classes, obj.Klass).TypeIndex, (int)obj.ShallowSize, childrenHolder);
            }

            // Build roots
            foreach (var (kind, childList) in rootKindToRefs)
            {
                var root = rootBuilder.FindOrCreateChild(stringTable[kind]);
                foreach (var nodeIndex in childList)
                    root.AddChild(nodeIndex);
            }

            rootBuilder.Build();
            memoryGraph.RootIndex = rootBuilder.Index;
            memoryGraph.AllowReading();

            return memoryGraph;
        }

        static Dictionary<string, double> LoadCounters(RiffChunk chunk)
        {
            var counters = new Dictionary<string, double>();
            var data = chunk.Data;
            while (data.Length > 0)
            {
                var name = DecodePString(ref data);
                var value = ReadDoubleValue(ref data);
                counters[name] = value;
            }
            return counters;
        }

        static Dictionary<ulong, string> DecodeStringTable(RiffChunk chunk)
        {
            var stringTable = new Dictionary<ulong, string>();
            var data = chunk.Data;
            while (data.Length > 0)
            {
                Assert(TryReadULEBInt(ref data, out var index));
                var value = DecodePString(ref data);
                stringTable[index] = value;
            }
            return stringTable;
        }

        static void DecodeClasses(RiffChunk chunk, SnapshotClass[] classes, ref int classOffset)
        {
            var data = chunk.Data;
            while (data.Length > 0)
            {
                ref var klass = ref classes[classOffset++];
                klass = new SnapshotClass();
                klass.Klass = BinaryPrimitives.ReadUInt32LittleEndian(data);
                klass.ElementKlass = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4));
                klass.NestingKlass = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8));
                klass.Assembly = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(12));
                data = data.Slice(16);
                Assert(TryReadULEBInt(ref data, out var rank));
                klass.Rank = (int)rank;
                Assert(TryReadULEBInt(ref data, out var kindName));
                klass.Kind = kindName;
                Assert(TryReadULEBInt(ref data, out var ns));
                klass.Namespace = ns;
                Assert(TryReadULEBInt(ref data, out var name));
                klass.Name = name;
                Assert(TryReadULEBInt(ref data, out var numGps));
                // FIXME: Slices of a single big array for better density
                // ALTERNATELY: On-demand constructed ReadOnlySpan over the mmap'd view
                var gps = new uint[numGps];
                for (uint i = 0; i < numGps; i++)
                {
                    gps[i] = BinaryPrimitives.ReadUInt32LittleEndian(data);
                    data = data.Slice(4);
                }
                klass.GenericParameters = gps;
            }
        }

        static void DecodeObjectHeaders(RiffChunk chunk, SnapshotObject[] objects, ref int objectOffset)
        {
            var data = chunk.Data;
            while (data.Length > 0)
            {
                ref var header = ref objects[objectOffset++];
                header = new SnapshotObject();
                header.Object = BinaryPrimitives.ReadUInt32LittleEndian(data); //ReadValue<uint>(ref data);
                header.Klass = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4)); //ReadValue<uint>(ref data);
                data = data.Slice(8);
                Assert(TryReadULEBInt(ref data, out var shallowSize));
                header.ShallowSize = shallowSize;
            }
        }

        static double ReadDoubleValue(ref ReadOnlySpan<byte> source)
        {
            var result = BinaryPrimitives.ReadDoubleLittleEndian(source);
            source = source.Slice(Unsafe.SizeOf<double>());
            return result;
        }

        /*static T ReadValue<T>(ref ReadOnlySpan<byte> source)
            where T : unmanaged, IBinaryInteger<T>, IMinMaxValue<T>
        {
            var result = T.ReadLittleEndian(source, T.IsNegative(T.MinValue));
            source = source.Slice(Unsafe.SizeOf<T>());
            return result;
        }*/

        static ReadOnlySpan<byte> SlicePString(ref ReadOnlySpan<byte> source)
        {
            if (!TryReadULEBInt(ref source, out var length))
                throw new EndOfStreamException();
            var result = source.Slice(0, (int)length);
            source = source.Slice((int)length);
            return result;
        }

        static string DecodePString(ref ReadOnlySpan<byte> source)
        {
            var bytes = SlicePString(ref source);
            return Encoding.UTF8.GetString(bytes);
        }

        static bool TryReadULEBInt(ref ReadOnlySpan<byte> source, out ulong result)
        {
            result = 0;
            int bytesRead = 0, shift = 0;

            for (int i = 0; i < source.Length; i++) {
                var b = source[i];
                var shifted = (ulong)(b & 0x7F) << shift;
                result |= shifted;

                if ((b & 0x80) == 0) {
                    bytesRead = i + 1;
                    source = source.Slice(bytesRead);
                    return true;
                }

                shift += 7;
            }

            return false;
        }

        static ref SnapshotClass Class(SnapshotClass[] classes, uint klass)
        {
            var needle = new SnapshotClass { Klass = klass };
            var index = Array.BinarySearch(classes, needle, SnapshotClass.AddressComparer.Instance);
            if (index < 0)
                throw new Exception($"Class not found: {klass}");
            return ref classes[index];
        }


        static void Assert([DoesNotReturnIf(false)] bool condition, [CallerArgumentExpression("condition")] string? message = null)
        {
            if (condition)
                return;

            throw new Exception($"Assertion failed: {message}");
        }

        ref struct RiffChunk
        {
            private byte[] rentedArray;
            public ReadOnlySpan<byte> ChunkId;
            public ReadOnlySpan<byte> Data;

            public RiffChunk(Stream inputStream)
            {
                Span<byte> chunkId = stackalloc byte[4]; 
                Span<byte> lengthSpan = stackalloc byte[4];

                inputStream.ReadExactly(chunkId);
                inputStream.ReadExactly(lengthSpan);
                int length = BinaryPrimitives.ReadInt32LittleEndian(lengthSpan);

                rentedArray = ArrayPool<byte>.Shared.Rent(length + 4);
                chunkId.CopyTo(rentedArray.AsSpan(0, 4));
                ChunkId = rentedArray.AsSpan(0, 4);
                inputStream.ReadExactly(rentedArray.AsSpan(4, length));
                Data = rentedArray.AsSpan(4, length);
            }

            public void Dispose()
            {
                ArrayPool<byte>.Shared.Return(rentedArray);
            }
        }

        struct SnapshotClass
        {
            public class AddressComparer : IComparer<SnapshotClass>
            {
                public static readonly AddressComparer Instance = new();
                public int Compare(SnapshotClass x, SnapshotClass y) => x.Klass.CompareTo(y.Klass);
            }

            public uint Klass, ElementKlass, NestingKlass, Assembly;
            public int Rank;
            public ulong Kind, Namespace, Name;
            public ArraySegment<uint> GenericParameters;

            public NodeTypeIndex TypeIndex;
            private string? _FullName;

            public string GetFullName(SnapshotClass[] classes, Dictionary<ulong, string> stringTable)
            {
                if (_FullName != null)
                    return _FullName;

                if (Namespace > 0)
                    _FullName = $"{stringTable[Namespace]}.{stringTable[Name]}";
                else
                    _FullName = stringTable[Name];

                if (NestingKlass > 0)
                    _FullName = $"{Class(classes, NestingKlass).GetFullName(classes, stringTable)}.{_FullName}";

                // FIXME: Optimize this
                if (GenericParameters.Count > 0)
                    _FullName += $"<{string.Join(", ", from gp in GenericParameters select Class(classes, gp).GetFullName(classes, stringTable))}>";

                if (Rank > 0)
                    _FullName += "[]";

                return _FullName;
            }
        }

        struct SnapshotObject
        {
            public class AddressComparer : IComparer<SnapshotObject>
            {
                public static readonly AddressComparer Instance = new();
                public int Compare(SnapshotObject x, SnapshotObject y) => x.Object.CompareTo(y.Object);
            }

            public uint Object, Klass;
            public ulong ShallowSize;
        }
    }
}
