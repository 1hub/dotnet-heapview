# dotnet-heapview-mcp

dotnet-heapview-mcp is a stdio Model Context Protocol server for managed heap dump analysis.

It exposes heap analysis tools backed by dotnet-heapview's graph model, including heap loading, summaries, class queries, instance queries, outgoing references, incoming references, paths to roots, GC roots, preserved counters, and top-object analysis.

## Installation

```powershell
dotnet tool install -g dotnet-heapview-mcp
```

## Supported Dump Formats

- `.gcdump`
- `.hprof`
- `.mono-heap`

## MCP Client Configuration

After installing the .NET tool, configure your MCP client to run:

```powershell
dotnet-heapview-mcp
```

### Codex

Add the server to your Codex MCP configuration:

```powershell
codex mcp add heapview dotnet-heapview-mcp
```

### Claude Code

Register the server with Claude Code:

```powershell
claude mcp add heapview dotnet-heapview-mcp
```

### GitHub Copilot

Add the server to your MCP configuration:

```json
{
  "servers": {
    "heapview": {
      "type": "stdio",
      "command": "dotnet-heapview-mcp"
    }
  }
}
```

## Tools

- `load_heap`: loads a `.gcdump`, `.hprof`, or `.mono-heap` file and returns its summary.
- `get_summary`: returns the summary of the loaded heap.
- `get_classes_by_max_instances_count`: lists classes by instance count.
- `get_classes_by_max_instances_size`: lists classes by total instance size.
- `get_classes_by_regexp`: lists classes matching a regular expression.
- `get_class_by_name`: returns class details by full type name.
- `get_class_by_id`: returns class details by internal class ID.
- `get_instance_by_id`: returns instance details by internal graph node ID.
- `get_object_references`: returns objects referenced by an instance.
- `get_references_to_object`: returns objects that reference an instance.
- `get_instances_by_class_name`: returns instances of an exact class name.
- `get_instances_by_class_regexp`: returns instances whose class name matches a regular expression.
- `get_biggest_objects`: returns the biggest objects by retained size.
- `get_path_to_root`: returns one shortest retainer path from an instance to a GC root.
- `get_gc_roots`: returns GC roots from the loaded heap.
- `get_counters`: returns counters preserved by the loaded dump, when present.
- `analyze_heap_dump`: loads a heap dump and returns top classes by instance count.

## Development

Run from source with:

```powershell
dotnet run --project src/OneHub.Tools.HeapView.Mcp
```
