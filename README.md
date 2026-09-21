# KHZ Sheet

Local-first spreadsheet engine with a C11 native core, .NET 8 bindings, and a WPF desktop shell.

The project is built for public benefit. Legal permissions are defined by the Apache License 2.0; that purpose statement does not modify the license.

## Implemented surface

- Exact `int64` rational arithmetic in the native core.
- Cell kinds: empty, rational, text, bool, spreadsheet error, and formula.
- Native formula IR/evaluator with references, ranges, arithmetic, `SUM`, `AVERAGE`, `MIN`, `MAX`, and integer-exponent `POW`.
- Dependency tracking and topological recalculation. Cycles are reported, not broken.
- SHA-256 cell commit chain with a bounded commit log and explicit audit coverage.
- Optional SQLite ledger storing commit hashes, not cell payloads.
- XLSX read/write support.
- OPC package editing that preserves untouched part payload bytes; ZIP-container byte identity is not claimed.
- .NET 8 P/Invoke bindings with ABI size/version checks.
- Windows WPF desktop application (`KHZ Sheet`, version `0.1.0`).

## Numeric contract

Arithmetic in the native model stays exact while the result is representable as `KhzRational`. Unsupported inexact operations are refused instead of silently falling back to floating point.

XLSX export is a separate boundary. When conversion to the file format cannot preserve a value exactly, the writer reports that through `KhzXlsxReport.lossy_cells`.

## Evidence on `main`

Every push to `main` runs:

- **Native CI / Ubuntu:** Release build, native `ctest`, both managed integration tests, and a separate ASAN + UBSAN build/test job.
- **Desktop Windows:** native shared-library build, WPF desktop build, then both managed integration tests against the Windows DLL produced by that build.

A green workflow is evidence for that commit. This README is not a substitute for a passing run.

## Build

Requirements:

- CMake 3.16+
- C11 compiler
- SQLite development library when `KHZ_ENABLE_LEDGER=ON`
- Python 3 for the generated XLSX fixture tests
- .NET 8 SDK for managed bindings/tests

```bash
cmake -S . -B build/release -DCMAKE_BUILD_TYPE=Release
cmake --build build/release --parallel
ctest --test-dir build/release --output-on-failure
```

### Sanitizers

```bash
cmake -S . -B build/sanitize \
  -DCMAKE_BUILD_TYPE=Debug \
  -DKHZ_ENABLE_ASAN=ON \
  -DKHZ_ENABLE_UBSAN=ON
cmake --build build/sanitize --parallel
ctest --test-dir build/sanitize --output-on-failure
```

If a requested sanitizer runtime cannot be linked, configuration fails instead of producing an unsanitized build.

### Managed integration tests

Build the native shared library first, then expose it to the runtime loader:

```bash
LD_LIBRARY_PATH="$PWD/build/release:${LD_LIBRARY_PATH:-}" \
  dotnet run --project tests/dotnet/KhzExactLiteral/KhzExactLiteral.csproj -c Release

LD_LIBRARY_PATH="$PWD/build/release:${LD_LIBRARY_PATH:-}" \
  dotnet run --project tests/dotnet/KhzFormulaCells/KhzFormulaCells.csproj -c Release
```

### Windows desktop

```powershell
./build-desktop.ps1
```

Use `./build-desktop.ps1 -Run` to launch after a successful build.

## Scope boundaries

KHZ Sheet does not claim Excel feature parity, layout/print fidelity, cloud collaboration, or ZIP-container byte-for-byte reproduction.

The SQLite ledger is an integrity log. Because it stores hashes rather than payloads, it cannot reconstruct spreadsheet values by itself.

## License

Apache License 2.0. See `LICENSE`.
