# Testing
CTest builds native failure, proof-tamper, XLSX writer/reader, malformed ZIP, ledger and self-test executables. Added C executables cover formula boundaries, numeric boundaries/properties, formula rollback, dependency retry/idempotence, ZIP framing, randomized incremental recalculation, formula-cache export and resilient XLSX ingestion.
`khz_xlsx_resilience_test` exercises namespace-prefixed worksheet/shared-string tags, missing row/cell references, XML numeric references, CDATA, SpreadsheetML `_xHHHH_` decoding including surrogate pairs, malformed shared strings, inline strings, native XLSX error cells, recoverable numeric/Boolean faults, unsupported formulas and exact preservation of serialized decimal numeric text.
The Fraction oracle is registered when Python and a shared library are built without ASAN. ASAN builds run the native numeric property test instead; loading an ASAN library into an ordinary Python process is not treated as a valid sanitizer invocation.
Run `ctest --test-dir build/release --output-on-failure`. For verbose assertion counts, add `-V`. `--repeat until-fail:3` checks repeat execution, not exhaustive coverage.
Windows managed integration: after `build-desktop.ps1`, prepend `build/desktop-native/Release` to PATH and run each project under `tests/dotnet` with `dotnet run --project <project.csproj> -c Release`.
`KhzBoundaryContracts` contains regression tests that failed against the original code and passed against the final build. Default mode checks dependency lifetime and disposal. Run the `race` and `bad-image` modes in separate processes after the desktop native library has been built:
```powershell
$env:PATH = (Resolve-Path build/desktop-native/Release).Path + ";" + $env:PATH
$project = "tests/dotnet/KhzBoundaryContracts/KhzBoundaryContracts.csproj"
dotnet run --project $project -c Release
dotnet run --project $project -c Release -- race (Resolve-Path build/desktop-native/Release/khz_sheet.dll).Path
dotnet run --project $project -c Release -- bad-image
```
No line/branch coverage percentage, exhaustive fuzz campaign, locale-key validation, full OOXML conformance, or formal memory-safety proof is claimed. Existing `khz_fuzz_test` is a bounded malformed-input test, not evidence of sustained coverage-guided fuzzing.

`khz_formula_extended_test` covers the added functions, comparisons, rounding boundaries, lazy branch evaluation and dependency recalculation. `khz_rational_oracle.py` adds independent Fraction checks for round/floor/ceiling. `khz_xlsx_resilience_test` also covers DTDs, malformed nesting/attributes/styles, dimensions and a truncated ZIP.

`KhzSubsystemContracts` verifies style/layout/theme/table/chart and row-only/column-only/combined freeze-pane round trips, rejected pane/import inputs, worksheet name/lifecycle/deep-duplicate behavior, exact managed/native formula parity, table totals/history/headers, native coordinate identity during sort, and real WPF sort/edit/error recovery, dimensions, theme bindings, chart rendering and automation descriptions. Set `KHZ_TEST_ARTIFACTS` to retain synthetic workbooks and PNG visual receipts; otherwise temporary artifacts are deleted. WPF `DataGrid` row freezing is not implemented or represented as active in the UI.

Run `scripts/verify-windows.ps1` and `scripts/verify-linux.sh` to collect command logs, exits, warning counts and CTest JUnit receipts. `dotnet test` is explicitly recorded but does not discover framework tests in these console projects. The assertion evidence comes from `dotnet run`.

`tests/khz_presentation_oracle.py <retained-artifact-directory>` optionally reads nine synthetic exports using an already installed openpyxl and verifies styles, dimensions, tables, chart ranges/caches and theme metadata. This independent reader check is not a substitute for actual Excel/LibreOffice visual verification.
