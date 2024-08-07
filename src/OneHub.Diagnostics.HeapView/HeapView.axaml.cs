using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Threading;
using Graphs;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace OneHub.Diagnostics.HeapView;

public partial class HeapView : UserControl
{
    private HeapSnapshot? heapSnapshot;

    private ObservableCollection<IMemoryNode> heapNodes;
    private ObservableCollection<IMemoryNode> retainersNodes;
    private ObservableCollection<Tuple<string, double>> countersNodes;

    private HierarchicalTreeDataGridSource<IMemoryNode> heapSource;
    private HierarchicalTreeDataGridSource<IMemoryNode> retainersSource;
    private FlatTreeDataGridSource<Tuple<string, double>> countersSource;

    private Subject<string?> searchTerm = new();

    public HeapView()
    {
        InitializeComponent();

        searchTerm.Throttle(TimeSpan.FromMilliseconds(500))
            .Select(v => v?.Trim())
            .DistinctUntilChanged()
            .Subscribe(st =>
            {
                Dispatcher.UIThread.Invoke(() =>
                {
                    if (string.IsNullOrWhiteSpace(st))
                        ReloadSnapshot(heapSnapshot);
                    else
                        ReloadSnapshot(
                            heapSnapshot,
                            (n, u) =>
                                n is not null
                                && (n.Contains(st, StringComparison.InvariantCultureIgnoreCase) || u.ToString("x").Contains(st, StringComparison.InvariantCultureIgnoreCase)));
                });
            });


        searchBox.TextChanging += (object? sender, TextChangingEventArgs e) => searchTerm.OnNext(searchBox.Text);

        heapNodes = new();
        retainersNodes = new();
        countersNodes = new();

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
                new TextColumn<IMemoryNode, int>("Count", x => x.Children.Count),
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

        countersSource = new FlatTreeDataGridSource<Tuple<string, double>>(countersNodes)
        {
            Columns =
            {
                new TextColumn<Tuple<string, double>, string>("Name", x => x.Item1),
                new TextColumn<Tuple<string, double>, double>("Value", x => x.Item2),
            }
        };

        heapSource.RowSelection!.SelectionChanged += RowSelection_SelectionChanged;

        heapTree.Source = heapSource;
        retainersTree.Source = retainersSource;
        countersGrid.Source = countersSource;
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

    private void ReloadSnapshot(HeapSnapshot? heapSnapshot, Func<string, ulong, bool>? filterFunc = null)
    {
        heapNodes.Clear();
        retainersNodes.Clear();
        countersNodes.Clear();

        if (heapSnapshot is not null)
        {
            var nodeStorage = heapSnapshot.MemoryGraph.AllocNodeStorage();
            var typeStorage = heapSnapshot.MemoryGraph.AllocTypeNodeStorage();
            var typeNodes = new Dictionary<NodeTypeIndex, GroupedTypeMemoryNode>();
            for (NodeIndex nodeIndex = 0; nodeIndex < heapSnapshot.MemoryGraph.NodeIndexLimit; nodeIndex++)
            {
                var node = heapSnapshot.MemoryGraph.GetNode(nodeIndex, nodeStorage);
                var name = heapSnapshot.MemoryGraph.GetType(node.TypeIndex, typeStorage).Name;
                var address = heapSnapshot.MemoryGraph.GetAddress(nodeIndex);

                if (filterFunc is not null && !filterFunc(name, address))
                    continue;

                if (node.Size > 0)
                {
                    if (!typeNodes.TryGetValue(node.TypeIndex, out var groupedTypeMemoryNode))
                        typeNodes.Add(node.TypeIndex, groupedTypeMemoryNode = new GroupedTypeMemoryNode { Name = name, Size = 0 });
                    groupedTypeMemoryNode.Size += (ulong)node.Size;
                    groupedTypeMemoryNode.RetainedSize += heapSnapshot.GetRetainedSize(nodeIndex);
                    groupedTypeMemoryNode.MutableChildren.Add(new MemoryNode(heapSnapshot, node, groupedTypeMemoryNode.Name));
                }
            }

            foreach (var topNode in typeNodes.Values)
                heapNodes.Add(topNode);

            if (heapSnapshot.Counters is not null)
            {
                foreach (var counter in heapSnapshot.Counters)
                    countersNodes.Add(new Tuple<string, double>(counter.Key, counter.Value));
            }
            countersTab.IsVisible = heapSnapshot.Counters is not null;

            // Hide tabs if there's only one
            managedHeapTab.IsVisible = countersTab.IsVisible;
            // Always select the first one
            tabs.SelectedIndex = 0;
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
                ReloadSnapshot(heapSnapshot);
            }
        }
    }

    public interface IMemoryNode
    {
        string Name { get; }
        ulong Size { get; }
        ulong RetainedSize { get; }
        int Depth { get; }
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