# KHZ Sheet
Local-first spreadsheet engine with a C11 native core, exact representable rational arithmetic, native formula evaluation/dependencies, predecessor-linked SHA-256 cell proofs, optional system SQLite ledger, narrow XLSX support, .NET 8 P/Invoke bindings and a Windows WPF client.

## Implemented boundaries
| Layer | Implementation | Verification boundary |
|---|---|---|
| Arithmetic | canonical int64 rational operations; private wide intermediates | native boundary/property tests and independent Fraction oracle |
| Formula | SUM, AVG/AVERAGE, MIN, MAX; arithmetic and bounded native parser | native/managed existing tests; not complete grammar equivalence |
| Dependencies | direct/range edges and filtered topological recalculation | failed-mutation regression; 100 random DAGs, 2,000 mutations |
| Integrity | ABI-96 cell proofs and optional SQLite metadata ledger | existing tamper tests; not encryption, identity authentication or backup |
| XLSX | supported native reader/writer subset; bounded ZIP validation | synthetic malformed/round-trip tests; not general Excel compatibility |
| Managed | P/Invoke and OPC utilities | lifetime/concurrency/malformed-image regressions pass; see ABI.md |
| Desktop | WPF shell with branding, proof-copy, Undo/Redo, Find Next, freeze, typography/colors/alignment/borders, underline/wrap, built-in number formats and visual table styling | local rendering + XLSX style serialization contract tests; see DESKTOP.md and FEATURE-GAP.md |
| Tables/charts/localization | not implemented as requested subsystems | not verified |

## Build and test
```sh
cmake -S . -B build/release -DCMAKE_BUILD_TYPE=Release
cmake --build build/release --parallel 4
ctest --test-dir build/release --output-on-failure
```
Disable the optional SQLite ledger explicitly with `-DKHZ_ENABLE_LEDGER=OFF` when its development package is unavailable. On Windows, use `pwsh -NoProfile -File build-desktop.ps1`.

## Contracts and evidence
Read BUILDING.md, TESTING.md, ARCHITECTURE.md, NUMERIC-MODEL.md, FORMULA-LANGUAGE.md, ABI.md, XLSX-BOUNDARY.md, OPC-PRESERVATION.md, PROOF-CONTRACT.md, LEDGER.md and PQC-RELEASE.md. TABLES.md, CHARTS.md, LOCALIZATION.md, ACCESSIBILITY.md and DESKTOP.md distinguish implemented surfaces from absent capabilities.
SECURITY.md and THREAT-MODEL.md describe remaining boundaries. PERFORMANCE.md contains measured results and methodology. RELEASE-CHECKLIST.md defines the unfulfilled release gates. CHANGELOG.md records this intervention. `evidence/20260929` contains normalized command evidence and hashes; private machine paths are not published.
This is a verified subset of the requested intervention, not a claim of production readiness, financial suitability or safe arbitrary-XLSX handling.
