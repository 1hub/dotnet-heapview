using System.Text;
using System.Text.RegularExpressions;
using Graphs;
using OneHub.Diagnostics.HeapView;

namespace OneHub.Tools.HeapView.Mcp;

internal sealed class HeapDumpService
{
    private readonly object gate = new();
    private LoadedHeap? current;

    public string LoadHeap(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("A heap dump path is required.", nameof(filePath));

        string fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Heap dump file not found.", fullPath);

        using var file = File.OpenRead(fullPath);
        var memoryStream = new MemoryStream(file.Length > int.MaxValue ? 0 : (int)file.Length);
        file.CopyTo(memoryStream);
        memoryStream.Position = 0;

        HeapSnapshot snapshot = Path.GetExtension(fullPath).ToLowerInvariant() switch
        {
            ".hprof" => HProfConverter.Convert(memoryStream),
            ".mono-heap" => MonoHeapSnapshotConverter.Convert(memoryStream),
            ".gcdump" => new HeapSnapshot(new GCHeapDump(memoryStream, Path.GetFileName(fullPath))),
            _ => new HeapSnapshot(new GCHeapDump(memoryStream, Path.GetFileName(fullPath))),
        };

        lock (gate)
        {
            current = new LoadedHeap(fullPath, snapshot);
        }

        return FormatSummary(GetSnapshot());
    }

    public IReadOnlyList<ClassStats> GetClassesByMaxInstancesCount(int from, int to)
    {
        return GetClassStats()
            .OrderByDescending(x => x.InstanceCount)
            .ThenByDescending(x => x.Size)
            .ThenBy(x => x.ClassName, StringComparer.Ordinal)
            .Slice(from, to);
    }

    public IReadOnlyList<ClassStats> GetClassesByMaxInstancesSize(int from, int to)
    {
        return GetClassStats()
            .OrderByDescending(x => x.Size)
            .ThenByDescending(x => x.InstanceCount)
            .ThenBy(x => x.ClassName, StringComparer.Ordinal)
            .Slice(from, to);
    }

    public IReadOnlyList<ClassStats> GetClassesByRegexp(string regexp, int from, int to)
    {
        var regex = new Regex(regexp, RegexOptions.CultureInvariant);
        return GetClassStats()
            .Where(x => regex.IsMatch(x.ClassName))
            .OrderBy(x => x.ClassName, StringComparer.Ordinal)
            .Slice(from, to);
    }

    public ClassStats? GetClassByName(string name)
    {
        return GetClassStats().FirstOrDefault(x => x.ClassName == name);
    }

    public ClassStats? GetClassById(long id)
    {
        return GetClassStats().FirstOrDefault(x => x.ClassId == id);
    }

    public InstanceInfo? GetInstanceById(long id)
    {
        var snapshot = GetSnapshot();
        if (!TryResolveNode(snapshot, id, out NodeIndex nodeIndex))
            return null;

        var graph = snapshot.MemoryGraph;
        var node = graph.GetNode(nodeIndex, graph.AllocNodeStorage());
        return BuildInstanceInfo(snapshot, node);
    }

    public IReadOnlyList<ReferenceInfo> GetObjectReferences(long id, int from, int to)
    {
        var snapshot = GetSnapshot();
        if (!TryResolveNode(snapshot, id, out NodeIndex nodeIndex))
            throw new InvalidOperationException($"Instance not found: {id}");

        var graph = snapshot.MemoryGraph;
        var node = graph.GetNode(nodeIndex, graph.AllocNodeStorage());
        var nodeStorage = graph.AllocNodeStorage();
        var refs = new List<ReferenceInfo>();

        for (NodeIndex childIndex = node.GetFirstChildIndex();
             childIndex != NodeIndex.Invalid;
             childIndex = node.GetNextChildIndex())
        {
            if (childIndex == nodeIndex)
                continue;

            refs.Add(BuildReferenceInfo(snapshot, graph.GetNode(childIndex, nodeStorage)));
        }

        return refs
            .OrderByDescending(x => x.RetainedSize)
            .ThenByDescending(x => x.Size)
            .Slice(from, to);
    }

    public IReadOnlyList<ReferenceInfo> GetReferencesToObject(long id, int from, int to)
    {
        var snapshot = GetSnapshot();
        if (!TryResolveNode(snapshot, id, out NodeIndex nodeIndex))
            throw new InvalidOperationException($"Instance not found: {id}");

        var refNode = snapshot.RefGraph.GetNode(nodeIndex);
        var graph = snapshot.MemoryGraph;
        var nodeStorage = graph.AllocNodeStorage();
        var refs = new List<ReferenceInfo>();

        for (NodeIndex childIndex = refNode.GetFirstChildIndex();
             childIndex != NodeIndex.Invalid;
             childIndex = refNode.GetNextChildIndex())
        {
            if (childIndex == nodeIndex)
                continue;

            var node = graph.GetNode(childIndex, nodeStorage);
            refs.Add(BuildReferenceInfo(snapshot, node));
        }

        return refs
            .OrderBy(x => x.Depth)
            .ThenByDescending(x => x.RetainedSize)
            .Slice(from, to);
    }

    public IReadOnlyList<InstanceInfo> GetInstancesByClassName(string className, int from, int to)
    {
        return GetInstances(x => x == className)
            .OrderByDescending(x => x.RetainedSize)
            .ThenByDescending(x => x.Size)
            .Slice(from, to);
    }

    public IReadOnlyList<InstanceInfo> GetInstancesByClassRegexp(string regexp, int from, int to)
    {
        var regex = new Regex(regexp, RegexOptions.CultureInvariant);
        return GetInstances(regex.IsMatch)
            .OrderBy(x => x.ClassName, StringComparer.Ordinal)
            .ThenByDescending(x => x.RetainedSize)
            .Slice(from, to);
    }

    public IReadOnlyList<InstanceInfo> GetBiggestObjects(int limit)
    {
        var snapshot = GetSnapshot();
        var graph = snapshot.MemoryGraph;
        var nodeStorage = graph.AllocNodeStorage();
        var results = new List<InstanceInfo>();

        for (NodeIndex nodeIndex = 0; nodeIndex < graph.NodeIndexLimit; nodeIndex++)
        {
            var node = graph.GetNode(nodeIndex, nodeStorage);
            if (node.Size > 0)
                results.Add(BuildInstanceInfo(snapshot, node));
        }

        return results
            .OrderByDescending(x => x.RetainedSize)
            .ThenByDescending(x => x.Size)
            .Take(Math.Max(0, limit))
            .ToArray();
    }

    public IReadOnlyList<ReferenceInfo> GetPathToRoot(long id, int maxDepth)
    {
        var snapshot = GetSnapshot();
        if (!TryResolveNode(snapshot, id, out NodeIndex nodeIndex))
            throw new InvalidOperationException($"Instance not found: {id}");

        maxDepth = Math.Max(1, maxDepth);

        var previous = new Dictionary<NodeIndex, NodeIndex>();
        var distances = new Dictionary<NodeIndex, int> { [nodeIndex] = 0 };
        var visited = new HashSet<NodeIndex> { nodeIndex };
        var queue = new Queue<NodeIndex>();
        queue.Enqueue(nodeIndex);

        while (queue.Count > 0)
        {
            var currentIndex = queue.Dequeue();
            if (currentIndex == snapshot.MemoryGraph.RootIndex)
                return BuildPath(snapshot, previous, nodeIndex, currentIndex);

            int nextDistance = distances[currentIndex] + 1;
            if (nextDistance > maxDepth)
                continue;

            var refNode = snapshot.RefGraph.GetNode(currentIndex);
            for (NodeIndex retainerIndex = refNode.GetFirstChildIndex();
                 retainerIndex != NodeIndex.Invalid;
                 retainerIndex = refNode.GetNextChildIndex())
            {
                if (!visited.Add(retainerIndex))
                    continue;

                previous[retainerIndex] = currentIndex;
                distances[retainerIndex] = nextDistance;
                queue.Enqueue(retainerIndex);
            }
        }

        return Array.Empty<ReferenceInfo>();
    }

    public IReadOnlyList<GcRootInfo> GetGcRoots(int from, int to)
    {
        var snapshot = GetSnapshot();
        var graph = snapshot.MemoryGraph;
        var root = graph.GetNode(graph.RootIndex, graph.AllocNodeStorage());
        var results = new List<GcRootInfo>();

        CollectRootChildren(snapshot, root, "", results);

        return results
            .OrderBy(x => x.Kind, StringComparer.Ordinal)
            .ThenBy(x => x.InstanceId)
            .Slice(from, to);
    }

    public string GetSummary()
    {
        return FormatSummary(GetSnapshot());
    }

    public IReadOnlyList<CounterInfo> GetCounters(int from, int to)
    {
        var snapshot = GetSnapshot();
        if (snapshot.Counters is null || snapshot.Counters.Count == 0)
            return Array.Empty<CounterInfo>();

        return snapshot.Counters
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => new CounterInfo(x.Key, x.Value))
            .Slice(from, to);
    }

    public string AnalyzeHeapDump(string filePath, int limit)
    {
        LoadHeap(filePath);
        var stats = GetClassesByMaxInstancesCount(0, limit);
        var sb = new StringBuilder();
        sb.Append("Top ").Append(stats.Count).AppendLine(" Classes in Heap Dump:");
        sb.AppendLine("Class Name | Count | Size");
        sb.AppendLine(new string('-', 75));

        foreach (var stat in stats)
            sb.Append(stat.ClassName).Append(" | ").Append(stat.InstanceCount).Append(" | ").Append(stat.Size).AppendLine();

        return sb.ToString();
    }

    private IReadOnlyList<ClassStats> GetClassStats()
    {
        var snapshot = GetSnapshot();
        var graph = snapshot.MemoryGraph;
        var nodeStorage = graph.AllocNodeStorage();
        var typeStorage = graph.AllocTypeNodeStorage();
        var stats = new Dictionary<NodeTypeIndex, MutableClassStats>();

        for (NodeIndex nodeIndex = 0; nodeIndex < graph.NodeIndexLimit; nodeIndex++)
        {
            var node = graph.GetNode(nodeIndex, nodeStorage);
            if (node.Size <= 0)
                continue;

            if (!stats.TryGetValue(node.TypeIndex, out var stat))
            {
                string typeName = graph.GetType(node.TypeIndex, typeStorage).Name;
                stat = new MutableClassStats((int)node.TypeIndex, typeName);
                stats.Add(node.TypeIndex, stat);
            }

            stat.InstanceCount++;
            stat.Size += (ulong)node.Size;
            stat.RetainedSize += snapshot.GetRetainedSize(nodeIndex);
        }

        return stats.Values
            .Select(x => new ClassStats(x.ClassId, x.ClassName, x.InstanceCount, x.Size, x.RetainedSize))
            .ToArray();
    }

    private IReadOnlyList<InstanceInfo> GetInstances(Func<string, bool> typePredicate)
    {
        var snapshot = GetSnapshot();
        var graph = snapshot.MemoryGraph;
        var nodeStorage = graph.AllocNodeStorage();
        var typeStorage = graph.AllocTypeNodeStorage();
        var instances = new List<InstanceInfo>();

        for (NodeIndex nodeIndex = 0; nodeIndex < graph.NodeIndexLimit; nodeIndex++)
        {
            var node = graph.GetNode(nodeIndex, nodeStorage);
            if (node.Size <= 0)
                continue;

            string typeName = graph.GetType(node.TypeIndex, typeStorage).Name;
            if (typePredicate(typeName))
                instances.Add(BuildInstanceInfo(snapshot, node));
        }

        return instances;
    }

    private HeapSnapshot GetSnapshot()
    {
        lock (gate)
        {
            return current?.Snapshot ?? throw new InvalidOperationException("No heap dump is loaded. Call load_heap first.");
        }
    }

    private static string FormatSummary(HeapSnapshot snapshot)
    {
        var graph = snapshot.MemoryGraph;
        var nodeStorage = graph.AllocNodeStorage();
        var typeIndexes = new HashSet<NodeTypeIndex>();
        long totalInstances = 0;
        ulong totalSize = 0;

        for (NodeIndex nodeIndex = 0; nodeIndex < graph.NodeIndexLimit; nodeIndex++)
        {
            var node = graph.GetNode(nodeIndex, nodeStorage);
            if (node.Size <= 0)
                continue;

            totalInstances++;
            totalSize += (ulong)node.Size;
            typeIndexes.Add(node.TypeIndex);
        }

        return $"Total Instances: {totalInstances}\nTotal Size: {totalSize} bytes\nTotal Classes: {typeIndexes.Count}\nTotal Graph Nodes: {(int)graph.NodeIndexLimit}";
    }

    private static bool TryResolveNode(HeapSnapshot snapshot, long id, out NodeIndex nodeIndex)
    {
        var graph = snapshot.MemoryGraph;
        if (id >= 0 && id < (int)graph.NodeIndexLimit)
        {
            nodeIndex = (NodeIndex)(int)id;
            return true;
        }

        var nodeStorage = graph.AllocNodeStorage();
        ulong address = unchecked((ulong)id);
        for (NodeIndex candidate = 0; candidate < graph.NodeIndexLimit; candidate++)
        {
            _ = graph.GetNode(candidate, nodeStorage);
            if (graph.GetAddress(candidate) == address)
            {
                nodeIndex = candidate;
                return true;
            }
        }

        nodeIndex = NodeIndex.Invalid;
        return false;
    }

    private static InstanceInfo BuildInstanceInfo(HeapSnapshot snapshot, Node node)
    {
        var graph = snapshot.MemoryGraph;
        var typeName = graph.GetType(node.TypeIndex, graph.AllocTypeNodeStorage()).Name;
        var references = new List<ReferenceInfo>();
        var childStorage = graph.AllocNodeStorage();

        for (NodeIndex childIndex = node.GetFirstChildIndex();
             childIndex != NodeIndex.Invalid;
             childIndex = node.GetNextChildIndex())
        {
            if (childIndex == node.Index)
                continue;

            var child = graph.GetNode(childIndex, childStorage);
            references.Add(BuildReferenceInfo(snapshot, child));
        }

        return new InstanceInfo(
            InstanceId: (int)node.Index,
            Address: graph.GetAddress(node.Index),
            ClassId: (int)node.TypeIndex,
            ClassName: typeName,
            Size: (ulong)Math.Max(0, node.Size),
            RetainedSize: snapshot.GetRetainedSize(node.Index),
            Depth: snapshot.GetDepth(node.Index),
            References: references);
    }

    private static ReferenceInfo BuildReferenceInfo(HeapSnapshot snapshot, Node node)
    {
        var graph = snapshot.MemoryGraph;
        return new ReferenceInfo(
            InstanceId: (int)node.Index,
            Address: graph.GetAddress(node.Index),
            ClassName: graph.GetType(node.TypeIndex, graph.AllocTypeNodeStorage()).Name,
            Size: (ulong)Math.Max(0, node.Size),
            RetainedSize: snapshot.GetRetainedSize(node.Index),
            Depth: snapshot.GetDepth(node.Index));
    }

    private static IReadOnlyList<ReferenceInfo> BuildPath(
        HeapSnapshot snapshot,
        Dictionary<NodeIndex, NodeIndex> previous,
        NodeIndex startIndex,
        NodeIndex endIndex)
    {
        var graph = snapshot.MemoryGraph;
        var nodeStorage = graph.AllocNodeStorage();
        var path = new List<ReferenceInfo>();

        for (NodeIndex index = endIndex; ; index = previous[index])
        {
            path.Add(BuildReferenceInfo(snapshot, graph.GetNode(index, nodeStorage)));
            if (index == startIndex)
                break;
        }

        path.Reverse();
        return path;
    }

    private static void CollectRootChildren(HeapSnapshot snapshot, Node node, string kind, List<GcRootInfo> results)
    {
        var graph = snapshot.MemoryGraph;
        var typeStorage = graph.AllocTypeNodeStorage();
        var childStorage = graph.AllocNodeStorage();

        string nodeName = graph.GetType(node.TypeIndex, typeStorage).Name;
        string childKind = string.IsNullOrEmpty(kind) ? nodeName : $"{kind}/{nodeName}";

        for (NodeIndex childIndex = node.GetFirstChildIndex();
             childIndex != NodeIndex.Invalid;
             childIndex = node.GetNextChildIndex())
        {
            var child = graph.GetNode(childIndex, childStorage);
            if (child.Size > 0)
            {
                results.Add(new GcRootInfo(
                    Kind: childKind,
                    InstanceId: (int)child.Index,
                    Address: graph.GetAddress(child.Index),
                    InstanceClassName: graph.GetType(child.TypeIndex, typeStorage).Name));
            }
            else
            {
                CollectRootChildren(snapshot, child, childKind, results);
            }
        }
    }

    private sealed record LoadedHeap(string FilePath, HeapSnapshot Snapshot);

    private sealed class MutableClassStats(int classId, string className)
    {
        public int ClassId { get; } = classId;
        public string ClassName { get; } = className;
        public int InstanceCount { get; set; }
        public ulong Size { get; set; }
        public ulong RetainedSize { get; set; }
    }
}

internal sealed record ClassStats(int ClassId, string ClassName, int InstanceCount, ulong Size, ulong RetainedSize);

internal sealed record ReferenceInfo(int InstanceId, ulong Address, string ClassName, ulong Size, ulong RetainedSize, int Depth);

internal sealed record InstanceInfo(
    int InstanceId,
    ulong Address,
    int ClassId,
    string ClassName,
    ulong Size,
    ulong RetainedSize,
    int Depth,
    IReadOnlyList<ReferenceInfo> References);

internal sealed record GcRootInfo(string Kind, int InstanceId, ulong Address, string InstanceClassName);

internal sealed record CounterInfo(string Name, double Value);

internal static class PaginationExtensions
{
    public static IReadOnlyList<T> Slice<T>(this IEnumerable<T> source, int from, int to)
    {
        from = Math.Max(0, from);
        to = Math.Max(from, to);
        return source.Skip(from).Take(to - from).ToArray();
    }
}
