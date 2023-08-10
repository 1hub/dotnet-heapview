using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Graphs;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace OneHub.Diagnostics.HeapView;

public partial class HeapView : UserControl
{
    private HeapSnapshot? heapSnapshot;

    private ObservableCollection<IMemoryNode> heapNodes;
    private ObservableCollection<IMemoryNode> retainersNodes;

    private HierarchicalTreeDataGridSource<IMemoryNode> heapSource;
    private HierarchicalTreeDataGridSource<IMemoryNode> retainersSource;

    public HeapView()
    {
        InitializeComponent();

        heapNodes = new();
        retainersNodes = new();

        TextColumn<IMemoryNode, ulong> retainedSizeColumn;

        heapSource = new HierarchicalTreeDataGridSource<IMemoryNode>(heapNodes)
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

        heapSource.SortBy(retainedSizeColumn, System.ComponentModel.ListSortDirection.Descending);

        TextColumn<IMemoryNode, int> distanceToRootColumn;

        retainersSource = new HierarchicalTreeDataGridSource<IMemoryNode>(retainersNodes)
        {
            Columns =
            {
                new HierarchicalExpanderColumn<IMemoryNode>(
                    new TextColumn<IMemoryNode, string>("Name", x => x.Name, GridLength.Star),
                    x => x.Children),
                (distanceToRootColumn = new TextColumn<IMemoryNode, int>("Distance to root", x => x.Depth)),
            },
        };

        retainersSource.SortBy(distanceToRootColumn, System.ComponentModel.ListSortDirection.Ascending);

        heapSource.RowSelection!.SelectionChanged += RowSelection_SelectionChanged;

        heapTree.Source = heapSource;
        retainersTree.Source = retainersSource;
    }

    private void RowSelection_SelectionChanged(object? sender, Avalonia.Controls.Selection.TreeSelectionModelSelectionChangedEventArgs<IMemoryNode> e)
    {
        retainersNodes.Clear();
        if (e.SelectedItems.FirstOrDefault() is MemoryNode memoryNode)
        {
            var node = memoryNode.HeapSnapshot.MemoryGraph.GetNode(memoryNode.NodeIndex, memoryNode.HeapSnapshot.MemoryGraph.AllocNodeStorage());
            var refMemoryNode = new MemoryNode(memoryNode.HeapSnapshot, node, childrenAreRetainers: true);
            retainersNodes.Add(refMemoryNode);
            retainersSource.Expand(new IndexPath(0));
        }
    }

    public HeapSnapshot? Snapshot
    {
        get => heapSnapshot;
        set
        {
            if (heapSnapshot != value)
            {
                heapSnapshot = value;

                heapNodes.Clear();
                retainersNodes.Clear();

                if (heapSnapshot is not null)
                {
                    var nodeStorage = heapSnapshot.MemoryGraph.AllocNodeStorage();
                    var typeStorage = heapSnapshot.MemoryGraph.AllocTypeNodeStorage();
                    var typeNodes = new Dictionary<NodeTypeIndex, GroupedTypeMemoryNode>();
                    for (NodeIndex nodeIndex = 0; nodeIndex < heapSnapshot.MemoryGraph.NodeIndexLimit; nodeIndex++)
                    {
                        var node = heapSnapshot.MemoryGraph.GetNode(nodeIndex, nodeStorage);
                        if (node.Size > 0)
                        {
                            if (!typeNodes.TryGetValue(node.TypeIndex, out var groupedTypeMemoryNode))
                                typeNodes.Add(node.TypeIndex, groupedTypeMemoryNode = new GroupedTypeMemoryNode { Name = heapSnapshot.MemoryGraph.GetType(node.TypeIndex, typeStorage).Name, Size = 0 });
                            groupedTypeMemoryNode.Size += (ulong)node.Size;
                            groupedTypeMemoryNode.RetainedSize += heapSnapshot.GetRetainedSize(nodeIndex);
                            groupedTypeMemoryNode.MutableChildren.Add(new MemoryNode(heapSnapshot, node, groupedTypeMemoryNode.Name));
                        }
                    }

                    foreach (var topNode in typeNodes.Values)
                        heapNodes.Add(topNode);
                }
            }
        }
    }

    public interface IMemoryNode
    {
        string Name { get; }
        ulong Size { get; }
        ulong RetainedSize { get; }
        int Depth { get; }
        IReadOnlyList <IMemoryNode> Children { get; }
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
            HeapSnapshot = heapSnapshot;
            NodeIndex = node.Index;
            ChildrenAreRetainers = childrenAreRetainers;
        }

        public HeapSnapshot HeapSnapshot { get; init; }
        public NodeIndex NodeIndex { get; init; }

        public string Name { get; init; }
        public ulong Size { get; init; }
        public ulong RetainedSize => HeapSnapshot.GetRetainedSize(NodeIndex);
        public int Depth => HeapSnapshot.GetDepth(NodeIndex);
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
        public int Depth => 0;
        public IReadOnlyList<IMemoryNode> Children => MutableChildren;
        public List<IMemoryNode> MutableChildren { get; } = new List<IMemoryNode>();
    }
}