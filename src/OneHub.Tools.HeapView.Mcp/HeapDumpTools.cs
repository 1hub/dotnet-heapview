using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace OneHub.Tools.HeapView.Mcp;

[McpServerToolType]
internal sealed class HeapDumpTools(HeapDumpService heapDumpService)
{
    [McpServerTool(Name = "load_heap", Title = "Load Heap Dump", ReadOnly = false)]
    [Description("Loads a .gcdump, .hprof, or .mono-heap heap dump file and returns its summary.")]
    public string LoadHeap(
        [Description("Path to the heap dump file.")]
        string file_path)
    {
        return heapDumpService.LoadHeap(file_path);
    }

    [McpServerTool(Name = "get_classes_by_max_instances_count", Title = "Get Classes By Max Instances Count", ReadOnly = true)]
    [Description("Returns a sorted list of classes by instance count descending with pagination.")]
    public string GetClassesByMaxInstancesCount(int from = 0, int to = 50)
    {
        return FormatClassStats(heapDumpService.GetClassesByMaxInstancesCount(from, to));
    }

    [McpServerTool(Name = "get_classes_by_max_instances_size", Title = "Get Classes By Max Instances Size", ReadOnly = true)]
    [Description("Returns a sorted list of classes by total instance size descending with pagination.")]
    public string GetClassesByMaxInstancesSize(int from = 0, int to = 50)
    {
        return FormatClassStats(heapDumpService.GetClassesByMaxInstancesSize(from, to));
    }

    [McpServerTool(Name = "get_classes_by_regexp", Title = "Get Classes By RegExp", ReadOnly = true)]
    [Description("Returns classes matching the regular expression with pagination.")]
    public string GetClassesByRegexp(string regexp, int from = 0, int to = 50)
    {
        return FormatClassStats(heapDumpService.GetClassesByRegexp(regexp, from, to));
    }

    [McpServerTool(Name = "get_class_by_name", Title = "Get Class By Name", ReadOnly = true)]
    [Description("Returns class details by full type name.")]
    public string GetClassByName(string name)
    {
        var stat = heapDumpService.GetClassByName(name);
        return stat is null ? $"Class not found: {name}" : FormatClassDetails(stat);
    }

    [McpServerTool(Name = "get_class_by_id", Title = "Get Class By ID", ReadOnly = true)]
    [Description("Returns class details by internal class/type ID.")]
    public string GetClassById(long id)
    {
        var stat = heapDumpService.GetClassById(id);
        return stat is null ? $"Class not found: {id}" : FormatClassDetails(stat);
    }

    [McpServerTool(Name = "get_instance_by_id", Title = "Get Instance By ID", ReadOnly = true)]
    [Description("Returns instance details by internal graph node ID, including class, size, retained size, depth, and outgoing reference count.")]
    public string GetInstanceById(long id)
    {
        var instance = heapDumpService.GetInstanceById(id);
        return instance is null ? $"Instance not found: {id}" : FormatInstance(instance);
    }

    [McpServerTool(Name = "get_object_references", Title = "Get Object References", ReadOnly = true)]
    [Description("Returns objects referenced by an instance, selected by internal graph node ID.")]
    public string GetObjectReferences(long id, int from = 0, int to = 50)
    {
        return FormatReferences(heapDumpService.GetObjectReferences(id, from, to));
    }

    [McpServerTool(Name = "get_references_to_object", Title = "Get References To Object", ReadOnly = true)]
    [Description("Returns objects that reference an instance, selected by internal graph node ID.")]
    public string GetReferencesToObject(long id, int from = 0, int to = 50)
    {
        return FormatReferences(heapDumpService.GetReferencesToObject(id, from, to));
    }

    [McpServerTool(Name = "get_instances_by_class_name", Title = "Get Instances By Class Name", ReadOnly = true)]
    [Description("Returns instances of an exact class/type name sorted by retained size.")]
    public string GetInstancesByClassName(string class_name, int from = 0, int to = 50)
    {
        return FormatInstances(heapDumpService.GetInstancesByClassName(class_name, from, to));
    }

    [McpServerTool(Name = "get_instances_by_class_regexp", Title = "Get Instances By Class RegExp", ReadOnly = true)]
    [Description("Returns instances whose class/type name matches a regular expression.")]
    public string GetInstancesByClassRegexp(string regexp, int from = 0, int to = 50)
    {
        return FormatInstances(heapDumpService.GetInstancesByClassRegexp(regexp, from, to));
    }

    [McpServerTool(Name = "get_biggest_objects", Title = "Get Biggest Objects", ReadOnly = true)]
    [Description("Returns the biggest objects by retained size.")]
    public string GetBiggestObjects(int limit = 50)
    {
        var instances = heapDumpService.GetBiggestObjects(limit);
        if (instances.Count == 0)
            return "No valid instances found.";

        var sb = new StringBuilder();
        foreach (var instance in instances)
        {
            sb.Append("ID: ").Append(instance.InstanceId)
                .Append(", Address: 0x").Append(instance.Address.ToString("x"))
                .Append(", Class: ").Append(instance.ClassName)
                .Append(", Size: ").Append(instance.Size)
                .Append(", Retained Size: ").Append(instance.RetainedSize)
                .AppendLine();
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "get_path_to_root", Title = "Get Path To Root", ReadOnly = true)]
    [Description("Returns one shortest retainer path from an instance to a GC root, selected by internal graph node ID.")]
    public string GetPathToRoot(long id, int max_depth = 100)
    {
        var path = heapDumpService.GetPathToRoot(id, max_depth);
        return path.Count == 0 ? "No path to root found within the requested depth." : FormatReferences(path);
    }

    [McpServerTool(Name = "get_gc_roots", Title = "Get GC Roots", ReadOnly = true)]
    [Description("Returns the GC roots of the loaded heap with pagination.")]
    public string GetGcRoots(int from = 0, int to = 50)
    {
        return FormatGcRoots(heapDumpService.GetGcRoots(from, to));
    }

    [McpServerTool(Name = "get_summary", Title = "Get Heap Summary", ReadOnly = true)]
    [Description("Returns the summary of the loaded heap.")]
    public string GetSummary()
    {
        return heapDumpService.GetSummary();
    }

    [McpServerTool(Name = "get_counters", Title = "Get Counters", ReadOnly = true)]
    [Description("Returns counters preserved by the loaded dump, when present.")]
    public string GetCounters(int from = 0, int to = 100)
    {
        var counters = heapDumpService.GetCounters(from, to);
        return counters.Count == 0
            ? "No counters are available for the loaded heap."
            : string.Join(Environment.NewLine, counters.Select(x => $"{x.Name}={x.Value}"));
    }

    [McpServerTool(Name = "analyze_heap_dump", Title = "Analyze Heap Dump", ReadOnly = false)]
    [Description("Parses a heap dump file and returns the top classes by instance count.")]
    public string AnalyzeHeapDump(string file_path, int limit = 10)
    {
        return heapDumpService.AnalyzeHeapDump(file_path, limit);
    }

    private static string FormatClassStats(IReadOnlyList<ClassStats> stats)
    {
        if (stats.Count == 0)
            return "No classes found.";

        return string.Join(
            Environment.NewLine,
            stats.Select(cs => $"{cs.ClassName} (ID: {cs.ClassId}, Count: {cs.InstanceCount}, Size: {cs.Size}, Retained Size: {cs.RetainedSize})"));
    }

    private static string FormatClassDetails(ClassStats stat)
    {
        return $"""
            Name: {stat.ClassName}
            ID: {stat.ClassId}
            Instances: {stat.InstanceCount}
            Total Size: {stat.Size}
            Total Retained Size: {stat.RetainedSize}
            """;
    }

    private static string FormatInstance(InstanceInfo instance)
    {
        var sb = new StringBuilder();
        sb.Append("Instance ID: ").Append(instance.InstanceId).AppendLine();
        sb.Append("Address: 0x").Append(instance.Address.ToString("x")).AppendLine();
        sb.Append("Class ID: ").Append(instance.ClassId).AppendLine();
        sb.Append("Class: ").Append(instance.ClassName).AppendLine();
        sb.Append("Size: ").Append(instance.Size).AppendLine();
        sb.Append("Retained Size: ").Append(instance.RetainedSize).AppendLine();
        sb.Append("Distance to Root: ").Append(instance.Depth).AppendLine();
        sb.Append("Outgoing References: ").Append(instance.References.Count).AppendLine();
        return sb.ToString();
    }

    private static string FormatInstances(IReadOnlyList<InstanceInfo> instances)
    {
        if (instances.Count == 0)
            return "No instances found.";

        var sb = new StringBuilder();
        foreach (var instance in instances)
        {
            sb.Append("ID: ").Append(instance.InstanceId)
                .Append(", Address: 0x").Append(instance.Address.ToString("x"))
                .Append(", Class: ").Append(instance.ClassName)
                .Append(", Size: ").Append(instance.Size)
                .Append(", Retained Size: ").Append(instance.RetainedSize)
                .Append(", Distance to Root: ").Append(instance.Depth)
                .Append(", Outgoing References: ").Append(instance.References.Count)
                .AppendLine();
        }

        return sb.ToString();
    }

    private static string FormatReferences(IReadOnlyList<ReferenceInfo> references)
    {
        if (references.Count == 0)
            return "No references found.";

        var sb = new StringBuilder();
        foreach (var reference in references)
        {
            sb.Append("Instance ID: ").Append(reference.InstanceId)
                .Append(", Address: 0x").Append(reference.Address.ToString("x"))
                .Append(", Class: ").Append(reference.ClassName)
                .Append(", Size: ").Append(reference.Size)
                .Append(", Retained Size: ").Append(reference.RetainedSize)
                .Append(", Distance to Root: ").Append(reference.Depth)
                .AppendLine();
        }

        return sb.ToString();
    }

    private static string FormatGcRoots(IReadOnlyList<GcRootInfo> roots)
    {
        if (roots.Count == 0)
            return "No GC roots found.";

        return string.Join(
            Environment.NewLine,
            roots.Select(root => $"Kind: {root.Kind}, Instance ID: {root.InstanceId}, Address: 0x{root.Address:x}, Class: {root.InstanceClassName}"));
    }
}
