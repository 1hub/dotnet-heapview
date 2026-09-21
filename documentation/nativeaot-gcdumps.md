# NativeAOT GC dump type names

## What PerfView does

PerfView separates dump deserialization from symbol lookup. Loading `GCHeapDump`
alone does not resolve native type names. The UI installs a
`MemoryGraph.ResolveTypeName` callback immediately after opening the dump:

- [GC dump loading](https://github.com/microsoft/perfview/blob/4aab31822f3329632a5b566fd8a286d08881760e/src/PerfView/PerfViewData.cs#L9193-L9197)
- [TypeNameSymbolResolver](https://github.com/microsoft/perfview/blob/4aab31822f3329632a5b566fd8a286d08881760e/src/PerfView/PerfViewData.cs#L10496-L10675)
- [Deferred graph names and fallback](https://github.com/microsoft/perfview/blob/4aab31822f3329632a5b566fd8a286d08881760e/src/MemoryGraph/graph.cs#L945-L970)

Its resolver requires a module path, PDB name, and PDB GUID, locates the native PDB,
then calls `NativeSymbolModule.FindNameForRva`. It removes the native vtable/EEType
decoration from the result. This implementation dates from Project N/.NET Native.
It does not resolve macOS dSYMs: the native symbol reader uses Windows DIA.

The collector stores `TypeNameID` as the type table's **module-relative address**,
not the live process address or a metadata token. Dynamic types can use their
template's address, so symbols cannot always reconstruct the exact constructed
runtime type. See the runtime's
[bulk type event producer](https://github.com/dotnet/runtime/blob/main/src/coreclr/nativeaot/Runtime/eventtrace_bulktype.cpp)
and PerfView's
[graph conversion](https://github.com/microsoft/perfview/blob/4aab31822f3329632a5b566fd8a286d08881760e/src/EtwHeapDump/DotNetHeapDumpGraphReader.cs#L560-L575).

## Implementation in heapview

`HeapSnapshot(GCHeapDump, ...)` resolves deferred names before building the
snapshot. `NativeAotTypeNameResolver` installs the callback temporarily, forces
name materialization, and restores the previous callback. Each module's symbol
table is read once and released after name resolution.

`MachOTypeSymbols` uses [LibObjectFile 2.3.1](https://www.nuget.org/packages/LibObjectFile/2.3.1)
to read the linked image or dSYM's load commands and native symbol table.
`UseSubStream` keeps section contents, including DWARF, out of memory; the symbol
table is decoded while the input stream is open. Subtracting the preferred
`__TEXT` address from a symbol's address gives the RVA stored in the dump; the
process's ASLR slide is irrelevant.
Only exact matches are accepted. NativeAOT's
[UnixNodeMangler](https://github.com/dotnet/runtime/blob/main/src/coreclr/tools/aot/ILCompiler.Compiler/Compiler/UnixNodeMangler.cs)
emits method tables as `_ZTV<length><compiler type name>`; Mach-O adds another
underscore. Shared GC-static layouts have `__GCStaticEEType_...` names.

Type names retain the compiler's spelling. For example, a DWARF class entry for
`System.RuntimeType` in the tested build also uses
`S_P_CoreLib_System_RuntimeType`; reading DWARF does not recover the original
namespace punctuation. Replacing all underscores with dots would corrupt
assembly names, nested types, and identifiers.

The desktop `--symbols` option and MCP `symbol_file_path` argument accept an
unstripped executable, dSYM bundle, or its DWARF file. Automatic lookup checks the
recorded module image and adjacent dSYM bundles. UUID and CPU identity are checked
against the local image when available. **The dump contains no macOS UUID**, so
the local image must still be the exact build used to collect it. Missing modules
are reported when an explicit symbol file cannot be verified.

If a recorded path contains trailing garbage including control characters, the
resolver can recover an existing file prefix. It does not rewrite the dump.
Missing automatic symbols retain `TypeID(...)`, including size/static suffixes;
returning null directly to the graph callback would otherwise lose the TypeID
when a suffix is present.

This implementation supports thin little-endian 64-bit Mach-O executables,
dylibs, and dSYMs with native symbol tables. Explicit symbols require a single
native module. Windows PDB, Linux ELF, universal Mach-O, and browser symbol upload
support remain future work.

Test fixtures use LibObjectFile's `MachOFile`, load-command/content model, and
writer. Version 2.3.1 exposes decoded symbols for reading but writable symbol
tables as raw content, so tests provide a small `MachOContent` implementation for
the `nlist_64` entries. Headers and load commands are generated and validated by
the library. Malformed-file tests truncate the generated files deliberately.

## Verification

On the supplied September 21, 2026 dump:

- 574,070 graph nodes and 42,806 type entries.
- 42,798 initially deferred entries; all resolve with the matching symbols.
- 42,607 ordinary type entries and 191 synthetic GC-static storage entries.
- The matching unstripped executable and a dSYM generated from it produce
  identical type-name sequences.
- Complete snapshot construction, including retained-size calculations, takes
  a few seconds on the development machine with LibObjectFile.

Regression tests cover ASLR-independent RVAs, unsigned high RVAs, exact address
matching, named types, fallback suffixes, path recovery, dSYM lookup, UUID
mismatch, missing/unsupported images, malformed tables, UTF-8 symbol-name lengths,
and module isolation.

```sh
dotnet test tests/OneHub.Diagnostics.HeapView.Tests
```
