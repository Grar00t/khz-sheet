# Release gate

The implemented scope is the bounded desktop/core feature set documented in README.md, TABLES.md, CHARTS.md and XLSX-BOUNDARY.md. It is not general Excel compatibility or approval for arbitrary untrusted workbooks.

Reproducible local gates:

- `pwsh -NoProfile -File scripts/verify-windows.ps1`: clean MSVC `/W4 /WX`, native CTest, WPF build with warnings as errors, all seven managed assertion runners, explicit `dotnet test` invocations, malformed-DLL/race modes, actual UI interactions and release executable startup.
- `bash scripts/verify-linux.sh [build-directory]`: clean GCC `-Wall -Wextra -Werror` with ledger enabled, and Clang ASAN+UBSAN with leak checking and ledger disabled.
- Machine receipts, source manifest, command exit codes, JUnit files, assertion summaries and log hashes are under `build/evidence-windows` and `build/evidence-linux`. Published normalized receipts identify the source version tested.

Managed projects are dependency-free console assertion runners. `dotnet run` executes their checks. `dotnet test` currently discovers no test-framework cases; its exit code alone is not test evidence.

Before wider release: independently validate each exported feature in target spreadsheet applications; assess actual screen-reader/keyboard/RTL workflows; complete the remaining ABI field-offset and ownership coverage; test long-lived edit resource use and fault injection; and measure real-user task completion. Unknown OPC payload preservation belongs to the low-level package editor, not desktop workbook reconstruction. Native proofs exclude formatting, layout, tables and charts. Hosted CI results are a separate gate from local receipts.
