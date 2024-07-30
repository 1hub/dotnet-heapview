using Graphs;
using System;
using System.Buffers.Binary;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using Avalonia.Controls;

namespace OneHub.Diagnostics.HeapView
{
    public class HProfConverter
    {
        public static MemoryGraph Convert(Stream inputStream)
        {
            MemoryGraph memoryGraph = new MemoryGraph(10000);
            var rootBuilder = new MemoryNodeBuilder(memoryGraph, "[Java Roots]");
            var staticRoot = rootBuilder.FindOrCreateChild("[static vars]");
            var otherRoots = rootBuilder.FindOrCreateChild("[other roots]");
            var childrenHolder = new GrowableArray<NodeIndex>();

            ReadOnlySpan<byte> JavaProfile10 = "JAVA PROFILE 1.0."u8;

            Span<byte> identifier = stackalloc byte[JavaProfile10.Length + 1 + 1];
            inputStream.ReadExactly(identifier);
            if (!identifier.StartsWith(JavaProfile10))
                throw new FormatException("Not a Java profile");
            // TODO: Check version

            int sizeOfIdentifier = (int)ReadUInt32();
            ulong timestamp = ((ulong)ReadUInt32() << 32) + ReadUInt32();
            Dictionary<ulong, string> stringTable = new();
            Dictionary<ulong, string> classNameTable = new();
            Dictionary<ulong, ClassLayout> classLayoutTable = new();
            Dictionary<ulong, NodeTypeIndex> classId2TypeIndex = new();
            Dictionary<HProfBasicType, NodeTypeIndex> basicType2TypeIndex = new();
            List<InstanceData> instances = new List<InstanceData>();

            while (inputStream.Position < inputStream.Length)
            {
                HProfTag tag = (HProfTag)ReadByte();
                uint relativeTimestamp = ReadUInt32();
                uint length = ReadUInt32();

                switch (tag)
                {
                    case HProfTag.Utf8String:
                        {
                            ulong id = ReadId();
                            int stringLength = (int)(length - sizeOfIdentifier);
                            var array = ArrayPool<byte>.Shared.Rent(stringLength);
                            inputStream.ReadExactly(array.AsSpan(0, stringLength));
                            string str = Encoding.UTF8.GetString(array.AsSpan(0, stringLength));
                            ArrayPool<byte>.Shared.Return(array);
                            stringTable[id] = str;
                        }
                        break;

                    case HProfTag.LoadClass:
                        {
                            _ = ReadUInt32(); // Class serial number
                            ulong classId = ReadId();
                            _ = ReadUInt32(); // Stack trace serial number
                            ulong classNameId = ReadId();
                            classNameTable[classId] = stringTable[classNameId];
                        }
                        break;

                    case HProfTag.HeapDump:
                    case HProfTag.HeapDumpSegment:
                        long savedPosition = inputStream.Position;
                        while (inputStream.Position < savedPosition + length)
                        {
                            HProfHeapDumpTag heapDumpTag = (HProfHeapDumpTag)ReadByte();
                            ulong objectId;
                            uint threadSerialNumber;
                            NodeIndex nodeIndex;
                            //Console.WriteLine(heapDumpTag);
                            switch (heapDumpTag)
                            {
                                case HProfHeapDumpTag.RootUnknown:
                                    objectId = ReadId();
                                    otherRoots.FindOrCreateChild("[unknown]").AddChild(memoryGraph.GetNodeIndex(objectId));
                                    break;

                                case HProfHeapDumpTag.RootJniGlobal:
                                    objectId = ReadId();
                                    ulong grefId = ReadId();
                                    otherRoots.FindOrCreateChild("[JNI global]").FindOrCreateChild($"{grefId:x} (JNI global)").AddChild(memoryGraph.GetNodeIndex(objectId));
                                    break;

                                case HProfHeapDumpTag.RootJniLocal:
                                case HProfHeapDumpTag.RootJavaFrame:
                                    objectId = ReadId();
                                    threadSerialNumber = ReadUInt32();
                                    uint frameNumber = ReadUInt32();
                                    rootBuilder.FindOrCreateChild($"[thread {threadSerialNumber:x}]").AddChild(memoryGraph.GetNodeIndex(objectId));
                                    break;

                                case HProfHeapDumpTag.RootNativeStack:
                                case HProfHeapDumpTag.RootThreadBlock:
                                    objectId = ReadId();
                                    threadSerialNumber = ReadUInt32();
                                    rootBuilder.FindOrCreateChild($"[thread {threadSerialNumber:x}]").AddChild(memoryGraph.GetNodeIndex(objectId));
                                    break;

                                case HProfHeapDumpTag.RootStickyClass:
                                    objectId = ReadId();
                                    otherRoots.FindOrCreateChild("[sticky class]").AddChild(memoryGraph.GetNodeIndex(objectId));
                                    break;

                                case HProfHeapDumpTag.RootThreadObject:
                                    objectId = ReadId();
                                    threadSerialNumber = ReadUInt32();
                                    _ = ReadUInt32(); // Stack trace serial number
                                    rootBuilder.FindOrCreateChild($"[thread {threadSerialNumber:x}]").AddChild(memoryGraph.GetNodeIndex(objectId));
                                    break;

                                case HProfHeapDumpTag.ClassDump:
                                    ulong classId = ReadId(); // Class object id
                                    _ = ReadUInt32(); // Stack trace serial number
                                    ulong superClassId = ReadId(); // Super class object id
                                    _ = ReadId(); // Class loader object id
                                    _ = ReadId(); // Signers object id
                                    _ = ReadId(); // Protection domain object id
                                    ReadId(); // Reserved
                                    ReadId(); // Reserved
                                    _ = ReadUInt32(); // Instance size
                                    ushort constPoolSize = ReadUInt16();
                                    for (int ic = 0; ic < constPoolSize; ic++)
                                    {
                                        ReadUInt16(); // Const pool index
                                        HProfBasicType type = (HProfBasicType)ReadByte();
                                        Skip(SizeOfElement(type));
                                    }
                                    ushort staticFieldCount = ReadUInt16();
                                    if (staticFieldCount > 0)
                                    {
                                        for (int isf = 0; isf < staticFieldCount; isf++)
                                        {
                                            var staticFieldName = stringTable[ReadId()];
                                            HProfBasicType type = (HProfBasicType)ReadByte();
                                            if (type == HProfBasicType.Object)
                                            {
                                                ulong staticId = ReadId();
                                                if (staticId != 0)
                                                    staticRoot.FindOrCreateChild($"{classNameTable[classId]}.{staticFieldName} (static var)").AddChild(memoryGraph.GetNodeIndex(staticId));
                                            }
                                            else
                                            {
                                                Skip(SizeOfElement(type));
                                            }
                                        }
                                    }
                                    ushort instanceFieldCount = ReadUInt16();
                                    HProfBasicType[] layoutTypes = new HProfBasicType[instanceFieldCount];
                                    for (int iif = 0; iif < instanceFieldCount; iif++)
                                    {
                                        var fieldName = ReadId();
                                        HProfBasicType type = (HProfBasicType)ReadByte();
                                        layoutTypes[iif] = type;
                                    }
                                    classLayoutTable[classId] = new ClassLayout(layoutTypes, superClassId);
                                    var typeIndex = memoryGraph.CreateType(classNameTable[classId]);
                                    classId2TypeIndex[classId] = typeIndex;
                                    break;

                                case HProfHeapDumpTag.InstanceDump:
                                    objectId = ReadId();
                                    _ = ReadUInt32(); // Stack trace serial number
                                    classId = ReadId();
                                    uint extraSize = ReadUInt32();
                                    byte[] data = new byte[extraSize];
                                    inputStream.ReadExactly(data);
                                    instances.Add(new InstanceData(objectId, classId, data));
                                    break;

                                case HProfHeapDumpTag.ObjectArrayDump:
                                    objectId = ReadId();
                                    _ = ReadUInt32(); // Stack trace serial number
                                    uint numElements = ReadUInt32();
                                    ReadId(); // Array class object id

                                    nodeIndex = memoryGraph.GetNodeIndex(objectId);
                                    childrenHolder.Clear();
                                    if (!basicType2TypeIndex.TryGetValue(HProfBasicType.Object, out typeIndex))
                                        basicType2TypeIndex.Add(HProfBasicType.Object, typeIndex = memoryGraph.CreateType($"Object[]"));
                                    for (int i = 0; i < numElements; i++)
                                    {
                                        var linkedObject = ReadId();
                                        if (linkedObject != 0)
                                            childrenHolder.Add(memoryGraph.GetNodeIndex(linkedObject));
                                    }
                                    memoryGraph.SetNode(nodeIndex, typeIndex, (int)(sizeOfIdentifier * numElements), childrenHolder);
                                    break;

                                case HProfHeapDumpTag.PrimitiveArrayDump:
                                case HProfHeapDumpTag.PrimitiveArrayNoData:
                                    objectId = ReadId();
                                    _ = ReadUInt32(); // Stack trace serial number
                                    numElements = ReadUInt32();
                                    HProfBasicType elementType = (HProfBasicType)ReadByte();

                                    if (heapDumpTag == HProfHeapDumpTag.PrimitiveArrayDump)
                                        Skip(SizeOfElement(elementType) * numElements);

                                    nodeIndex = memoryGraph.GetNodeIndex(objectId);
                                    childrenHolder.Clear();
                                    if (!basicType2TypeIndex.TryGetValue(elementType, out typeIndex))
                                        basicType2TypeIndex.Add(elementType, typeIndex = memoryGraph.CreateType($"{elementType}[]"));
                                    memoryGraph.SetNode(nodeIndex, typeIndex, (int)(SizeOfElement(elementType) * numElements), childrenHolder);
                                    break;

                                case HProfHeapDumpTag.RootInternedString:
                                    otherRoots.FindOrCreateChild("[interned string]").AddChild(memoryGraph.GetNodeIndex(ReadId()));
                                    break;

                                case HProfHeapDumpTag.RootFinalizing:
                                    otherRoots.FindOrCreateChild("[finalizing]").AddChild(memoryGraph.GetNodeIndex(ReadId()));
                                    break;

                                case HProfHeapDumpTag.RootDebugger:
                                    otherRoots.FindOrCreateChild("[debugger]").AddChild(memoryGraph.GetNodeIndex(ReadId()));
                                    break;

                                case HProfHeapDumpTag.RootReferenceCleanup:
                                    otherRoots.FindOrCreateChild("[reference cleanup]").AddChild(memoryGraph.GetNodeIndex(ReadId()));
                                    break;

                                case HProfHeapDumpTag.RootVMInternal:
                                    otherRoots.FindOrCreateChild("[VM internal]").AddChild(memoryGraph.GetNodeIndex(ReadId()));
                                    break;

                                case HProfHeapDumpTag.RootJNIMonitor:
                                    otherRoots.FindOrCreateChild("[JNI monitor]").AddChild(memoryGraph.GetNodeIndex(ReadId()));
                                    ReadUInt32();
                                    ReadUInt32();
                                    break;

                                case HProfHeapDumpTag.Unreachable:
                                    otherRoots.FindOrCreateChild("[unreachable]").AddChild(memoryGraph.GetNodeIndex(ReadId()));
                                    break;

                                case HProfHeapDumpTag.HeapDumpInfo:
                                    ReadUInt32();
                                    ReadUInt32();
                                    break;

                                default:
                                    throw new FormatException("Abort mission");
                            }
                        }
                        break;

                    default:
                        //Console.WriteLine(tag);
                        Skip(length);
                        break;
                }
            }

            foreach (var instance in instances)
            {
                NodeIndex nodeIndex = memoryGraph.GetNodeIndex(instance.ObjectId);
                int objectSize = 0;
                childrenHolder.Clear();
                ulong classId = instance.ClassId;
                while (classId != 0)
                {
                    var layout = classLayoutTable[classId];
                    foreach (var basicType in layout.Types)
                    {
                        uint elementSize = SizeOfElement(basicType);
                        if (basicType == HProfBasicType.Object)
                        {
                            // Create a link
                            var linkedObject = sizeOfIdentifier == 4 ?
                                BinaryPrimitives.ReadUInt32BigEndian(instance.Data.AsSpan(objectSize, 4)) :
                                BinaryPrimitives.ReadUInt64BigEndian(instance.Data.AsSpan(objectSize, 8));
                            if (linkedObject != 0)
                                childrenHolder.Add(memoryGraph.GetNodeIndex(linkedObject));
                        }
                        objectSize += (int)elementSize;
                    }
                    classId = layout.SuperClassId;
                }
                memoryGraph.SetNode(nodeIndex, classId2TypeIndex[instance.ClassId], objectSize, childrenHolder);
            }

            rootBuilder.Build();
            memoryGraph.RootIndex = rootBuilder.Index;
            memoryGraph.AllowReading();

            using (var w = new StreamWriter("d:\\temp\\graph.txt"))
                memoryGraph.WriteXml(w);

            return memoryGraph;


            byte ReadByte()
            {
                int result = inputStream.ReadByte();
                if (result < 0)
                    throw new EndOfStreamException();
                return (byte)result;
            }

            uint ReadUInt32()
            {
                Span<byte> buffer = stackalloc byte[4];
                inputStream.ReadExactly(buffer);
                return BinaryPrimitives.ReadUInt32BigEndian(buffer);
            }

            ushort ReadUInt16()
            {
                Span<byte> buffer = stackalloc byte[2];
                inputStream.ReadExactly(buffer);
                return BinaryPrimitives.ReadUInt16BigEndian(buffer);
            }

            ulong ReadId()
            {
                Span<byte> buffer = stackalloc byte[sizeOfIdentifier];
                inputStream.ReadExactly(buffer);
                return sizeOfIdentifier == 4 ? BinaryPrimitives.ReadUInt32BigEndian(buffer) : BinaryPrimitives.ReadUInt64BigEndian(buffer);
            }

            void Skip(uint length)
            {
                inputStream.Seek(length, SeekOrigin.Current);
            }

            uint SizeOfElement(HProfBasicType elementType)
            {
                return elementType switch
                {
                    HProfBasicType.Object => (uint)sizeOfIdentifier,
                    HProfBasicType.Boolean => 1,
                    HProfBasicType.Char => 2,
                    HProfBasicType.Float => 4,
                    HProfBasicType.Double => 8,
                    HProfBasicType.Byte => 1,
                    HProfBasicType.Short => 2,
                    HProfBasicType.Int => 4,
                    HProfBasicType.Long => 8,
                    _ => throw new FormatException("Unknown element type")
                };
            }

        }

        enum HProfTag
        {
            Utf8String = 1,
            LoadClass = 2,
            UnloadClass = 3,
            StackFrame = 4,
            StackTrace = 5,
            AllocSites = 6,
            HeapSummary = 7,
            StartThread = 10,
            EndThread = 11,
            HeapDump = 12,
            HeapDumpSegment = 28,
            HeapDumpEnd = 44,
            CpuSamples = 13,
            ControlSettings = 14,
        }

        enum HProfHeapDumpTag
        {
            RootUnknown = 255,
            RootJniGlobal = 1,
            RootJniLocal = 2,
            RootJavaFrame = 3,
            RootNativeStack = 4,
            RootStickyClass = 5,
            RootThreadBlock = 6,
            RootMonitorUsed = 7,
            RootThreadObject = 8,
            ClassDump = 32,
            InstanceDump = 33,
            ObjectArrayDump = 34,
            PrimitiveArrayDump = 35,

            HeapDumpInfo = 254,
            RootInternedString = 137,
            RootFinalizing = 138,
            RootDebugger = 139,
            RootReferenceCleanup = 140,
            RootVMInternal = 141,
            RootJNIMonitor = 142,
            Unreachable = 144,
            PrimitiveArrayNoData = 195,
        }

        enum HProfBasicType
        {
            Object = 2,
            Boolean = 4,
            Char = 5,
            Float = 6,
            Double = 7,
            Byte = 8,
            Short = 9,
            Int = 10,
            Long = 11,
        };

        record ClassLayout(HProfBasicType[] Types, ulong SuperClassId);
        record InstanceData(ulong ObjectId, ulong ClassId, byte[] Data);
    }
}
