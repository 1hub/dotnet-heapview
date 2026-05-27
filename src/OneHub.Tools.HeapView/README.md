# dotnet-heapview

dotnet-heapview is a desktop viewer for managed heap dump files.

![Screenshot of the dotnet-heapview user interface](../../documentation/screenshot.png)

## Installation

```powershell
dotnet tool install -g dotnet-heapview
```

## Usage

Open a dump from the command line:

```powershell
dotnet-heapview <path-to-dump>
```

You can also start the tool without an argument and choose a dump file from the file picker.

## Supported Dump Formats

- `.gcdump`
- `.hprof`
- `.mono-heap`

## Development

Run from source with:

```powershell
dotnet run --project src/OneHub.Tools.HeapView -- <path-to-dump>
```
