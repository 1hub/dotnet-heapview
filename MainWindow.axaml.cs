using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Graphs;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace OneHub.Tools.HeapView;

public partial class MainWindow : Window
{
    HeapSnapshot heapSnapshot;

    public MainWindow()
    {
        InitializeComponent();

        var heapDump = new GCHeapDump("C:\\Users\\filip_pq4cffv\\Documents\\heap-heap-hurray\\Test\\input.gcdump"); // "C:\\Users\\filip_pq4cffv\\Downloads\\my-dev-port-1688140697.gcdump");
        heapSnapshot = new HeapSnapshot(heapDump);

        heapNodes = new();
        retainersNodes = new();

        var nodeStorage = heapDump.MemoryGraph.AllocNodeStorage();
        var typeStorage = heapDump.MemoryGraph.AllocTypeNodeStorage();
        var typeNodes = new Dictionary<NodeTypeIndex, GroupedTypeMemoryNode>();
        for (NodeIndex nodeIndex = 0; nodeIndex < heapDump.MemoryGraph.NodeIndexLimit; nodeIndex++)
        {
            var node = heapDump.MemoryGraph.GetNode(nodeIndex, nodeStorage);
            if (node.Size > 0)
            {
                if (!typeNodes.TryGetValue(node.TypeIndex, out var groupedTypeMemoryNode))
                    typeNodes.Add(node.TypeIndex, groupedTypeMemoryNode = new GroupedTypeMemoryNode { Name = heapDump.MemoryGraph.GetType(node.TypeIndex, typeStorage).Name, Size = 0 });
                groupedTypeMemoryNode.Size += (ulong)node.Size;
                groupedTypeMemoryNode.RetainedSize += heapSnapshot.GetRetainedSize(nodeIndex);
                groupedTypeMemoryNode.MutableChildren.Add(new MemoryNode(heapSnapshot, node, groupedTypeMemoryNode.Name));
            }
        }

        foreach (var topNode in typeNodes.Values)
            heapNodes.Add(topNode);

        TextColumn<IMemoryNode, ulong> retainedSizeColumn;

        HeapSource = new HierarchicalTreeDataGridSource<IMemoryNode>(heapNodes)
        {
            Columns =
            {
                new HierarchicalExpanderColumn<IMemoryNode>(
                    new TextColumn<IMemoryNode, string>("Name", x => x.Name, GridLength.Star),
                    x => x.Children),
                new TextColumn<IMemoryNode, ulong>("Size", x => x.Size),
                (retainedSizeColumn = new TextColumn<IMemoryNode, ulong>("Retained Size", x => x.RetainedSize)),
            },
        };

        HeapSource.SortBy(retainedSizeColumn, System.ComponentModel.ListSortDirection.Descending);

        RetainersSource = new HierarchicalTreeDataGridSource<IMemoryNode>(retainersNodes)
        {
            Columns =
            {
                new HierarchicalExpanderColumn<IMemoryNode>(
                    new TextColumn<IMemoryNode, string>("Name", x => x.Name, GridLength.Star),
                    x => x.Children),
            },
        };

        heapTree.Source = HeapSource;
        retainersTree.Source = RetainersSource;
        HeapSource.RowSelection!.SelectionChanged += RowSelection_SelectionChanged;
    }

    private void RowSelection_SelectionChanged(object? sender, Avalonia.Controls.Selection.TreeSelectionModelSelectionChangedEventArgs<IMemoryNode> e)
    {
        retainersNodes.Clear();
        if (e.SelectedItems.FirstOrDefault() is MemoryNode memoryNode)
        {
            var node = memoryNode.HeapSnapshot.MemoryGraph.GetNode(memoryNode.NodeIndex, memoryNode.HeapSnapshot.MemoryGraph.AllocNodeStorage());
            var refMemoryNode = new MemoryNode(memoryNode.HeapSnapshot, node, childrenAreRetainers: true);
            retainersNodes.Add(refMemoryNode);
            RetainersSource.Expand(new IndexPath(0));
        }
    }

    private ObservableCollection<IMemoryNode> heapNodes;
    private ObservableCollection<IMemoryNode> retainersNodes;

    public HierarchicalTreeDataGridSource<IMemoryNode> HeapSource { get; }
    public HierarchicalTreeDataGridSource<IMemoryNode> RetainersSource { get; }

    public interface IMemoryNode
    {
        string Name { get; }
        ulong Size { get; }
        ulong RetainedSize { get; }
        IReadOnlyList<IMemoryNode> Children { get; }
    }

    class MemoryNode : IMemoryNode
    {
        public MemoryNode(HeapSnapshot heapSnapshot, Node node, string? name = null, bool childrenAreRetainers = false)
        {
            if (name == null)
            {
                name = heapSnapshot.MemoryGraph.GetType(node.TypeIndex, heapSnapshot.MemoryGraph.AllocTypeNodeStorage()).Name;
            }
            var address = heapSnapshot.MemoryGraph.GetAddress(node.Index);
            Name = address > 0 ? $"{name} @ {address:x}" : name;
            Size = (ulong)node.Size;
            RetainedSize = heapSnapshot.GetRetainedSize(node.Index);
            HeapSnapshot = heapSnapshot;
            NodeIndex = node.Index;
            ChildrenAreRetainers = childrenAreRetainers;
        }

        public HeapSnapshot HeapSnapshot { get; init; }
        public NodeIndex NodeIndex { get; init; }

        public string Name { get; init; }
        public ulong Size { get; init; }
        public ulong RetainedSize { get; init; }
        public bool ChildrenAreRetainers { get; init; }

        public IReadOnlyList<IMemoryNode> Children
        {
            get
            {
                if (children == null)
                {
                    children = new List<IMemoryNode>();
                    if (!ChildrenAreRetainers)
                    {
                        var node = HeapSnapshot.MemoryGraph.GetNode(NodeIndex, HeapSnapshot.MemoryGraph.AllocNodeStorage());
                        var nodeStorage = HeapSnapshot.MemoryGraph.AllocNodeStorage();

                        for (NodeIndex childIndex = node.GetFirstChildIndex();
                             childIndex != NodeIndex.Invalid;
                             childIndex = node.GetNextChildIndex())
                        {
                            if (childIndex != NodeIndex)
                                children.Add(new MemoryNode(HeapSnapshot, HeapSnapshot.MemoryGraph.GetNode(childIndex, nodeStorage)));
                        }
                    }
                    else
                    {
                        var node = HeapSnapshot.RefGraph.GetNode(NodeIndex);
                        var nodeStorage = HeapSnapshot.MemoryGraph.AllocNodeStorage();

                        for (NodeIndex childIndex = node.GetFirstChildIndex();
                             childIndex != NodeIndex.Invalid;
                             childIndex = node.GetNextChildIndex())
                        {
                            if (childIndex != NodeIndex)
                                children.Add(new MemoryNode(HeapSnapshot, HeapSnapshot.MemoryGraph.GetNode(childIndex, nodeStorage), childrenAreRetainers: true));
                        }
                    }
                }
                return children;
            }
        }

        List<IMemoryNode>? children;
    }

    class GroupedTypeMemoryNode : IMemoryNode
    {
        public required string Name { get; init; }
        public ulong Size { get; set; } = 0;
        public ulong RetainedSize { get; set; } = 0;
        public IReadOnlyList<IMemoryNode> Children => MutableChildren;
        public List<IMemoryNode> MutableChildren { get; } = new List<IMemoryNode>();
    }
}