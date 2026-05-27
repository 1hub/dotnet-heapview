# dotnet-heapview

dotnet-heapview is a set of tools for inspecting managed heap dumps.

## Tools

- [dotnet-heapview](src/OneHub.Tools.HeapView/README.md) is the desktop heap dump viewer.
- [dotnet-heapview-mcp](src/OneHub.Tools.HeapView.Mcp/README.md) is a Model Context Protocol server for heap analysis from AI coding agents and MCP clients.

The shared heap model and dump converters live in `src/OneHub.Diagnostics.HeapView`.

## Supported Dump Formats

- `.gcdump`
- `.hprof`
- `.mono-heap`

## Development

Build the solution with:

```powershell
dotnet build src/src.sln
```

Run the desktop viewer from source with:

```powershell
dotnet run --project src/OneHub.Tools.HeapView -- <path-to-dump>
```

Run the MCP server from source with:

```powershell
dotnet run --project src/OneHub.Tools.HeapView.Mcp
```
