using Graphs;
using Microsoft.Diagnostics.Runtime;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace OneHub.Diagnostics.HeapView
{
    public class MemoryDump
    {
        MemoryGraph memoryGraph;

        public MemoryDump(string path)
        {
            using var dataTarget = DataTarget.LoadDump(path);
            using var runtime = dataTarget.ClrVersions.Single().CreateRuntime();
            var heap = runtime.Heap;

            if (!heap.CanWalkHeap)
                throw new NotSupportedException("Cannot walk heap");

            memoryGraph = new(10000);

            var rootBuilder = new MemoryNodeBuilder(memoryGraph, "[.NET Roots]");
            var otherRoots = rootBuilder.FindOrCreateChild("[other roots]");
            var stack = new Stack<ClrObject>();
            var visitedObjects = new HashSet<ulong>();
            var childrenHolder = new GrowableArray<NodeIndex>();
            var clrType2TypeIndex = new Dictionary<ClrType, NodeTypeIndex>();

            foreach (var root in heap.EnumerateRoots())
            {
                string rootName = root.RootKind switch
                {
                    ClrRootKind.Stack => "[local vars]",
                    ClrRootKind.RefCountedHandle => "[COM/WinRT Objects]",
                    ClrRootKind.FinalizerQueue => "[finalizer Handles]",
                    ClrRootKind.PinnedHandle => "[pinning Handles]",
                    ClrRootKind.StrongHandle => "[strong Handles]",
                    _ => "[other Handles]",
                };
                var parent = root.RootKind == ClrRootKind.Stack ? rootBuilder : otherRoots;
                // TODO: Static variables?
                parent.FindOrCreateChild(rootName).AddChild(memoryGraph.GetNodeIndex(root.Address));

                if (!visitedObjects.Contains(root.Object.Address))
                {
                    stack.Push(root.Object);
                    visitedObjects.Add(root.Object.Address);
                    while (stack.Count > 0)
                    {
                        var currentObject = stack.Pop();

                        // Create graph node for this object
                        var type = heap.GetObjectType(currentObject);
                        if (type is not null)
                        {
                            if (!clrType2TypeIndex.TryGetValue(type, out NodeTypeIndex typeIndex))
                            {
                                // TODO: Sized types
                                typeIndex = memoryGraph.CreateType(type.Name, type.Module?.Name);
                                clrType2TypeIndex[type] = typeIndex;
                            }

                            NodeIndex nodeIndex = memoryGraph.GetNodeIndex(currentObject.Address);
                            ulong objSize = currentObject.Size;
                            Debug.Assert(objSize < 0x1000000000);
                            childrenHolder.Clear();
                            foreach (var referencedObject in currentObject.EnumerateReferences())
                            {
                                if (!visitedObjects.Contains((ulong)referencedObject.Address))
                                {
                                    visitedObjects.Add(referencedObject.Address);
                                    stack.Push(referencedObject);
                                }
                                childrenHolder.Add(memoryGraph.GetNodeIndex(referencedObject.Address));
                            }
                            memoryGraph.SetNode(nodeIndex, typeIndex, (int)objSize, childrenHolder);
                            Debug.Assert(memoryGraph.NodeCount < 10_000_000);
                        }
                    }
                }
            }

            // TODO: RCW?

            memoryGraph.RootIndex = rootBuilder.Index;
            memoryGraph.AllowReading();

            Snapshot = new HeapSnapshot(memoryGraph);
        }

        public HeapSnapshot Snapshot { get; private set; } 
    }
}
