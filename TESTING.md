# Testing
CTest builds native failure, proof-tamper, XLSX writer/reader, malformed ZIP, ledger and self-test executables. Five added C executables cover formula boundaries, numeric boundaries/properties, formula rollback, ZIP framing, and randomized incremental recalculation.
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

No line/branch coverage percentage, exhaustive fuzz campaign, desktop UI automation, table/chart testing, locale-key validation or formal memory-safety proof is claimed. Existing `khz_fuzz_test` is a bounded malformed-input test, not evidence of sustained coverage-guided fuzzing.
