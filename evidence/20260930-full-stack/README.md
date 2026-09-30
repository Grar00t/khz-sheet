# Full-stack enhancement receipts — 2026-09-30

Implementation commit: `5b84d2b563775b85ff14b26f6b857858e73242e5`. Tests ran against the working tree based on `b7024c2364357bdf8714f5f8a2c391353e354764`. Before collecting these receipts, every raw file hash in `windows/source-manifest.json` was checked against the committed worktree. Source hashes identify the exercised input bytes; they do not establish correctness on their own.

| Executed gate | Result |
|---|---|
| Windows / MSVC native CTest | 17/17 passed |
| WSL / GCC with SQLite ledger | 18/18 passed |
| WSL / Clang ASAN + UBSAN, leak checking, ledger off | 16/16 passed |
| Managed console assertion runners | 7/7 passed |
| New subsystem and actual WPF interaction assertions | 215 passed, 0 failures |
| Independent openpyxl 3.1.5 reader | 9 workbooks, 259 checks, 0 failures |
| Compiler warnings | 0 |
| Release executable startup | Responsive main window, then orderly close |

`summary.json` includes hashes of the executable, managed assemblies and installed native DLL. Each platform folder contains the full normalized logs, exit codes, command arguments and CTest JUnit files. Receipt log hashes refer to the published normalized text; original run-log hashes are retained separately. The Windows source manifest records actual file bytes, including platform checkout line endings.

The managed projects are console assertion programs. All seven `dotnet test` commands exited zero but discovered no test-framework cases. Their actual behavior checks were executed with `dotnet run`. Additional managed boundary race and bad-image modes also passed. The independent Fraction oracle in CTest executed 204,090 checks.

`ui/` contains actual WPF renders of all themes, the minimum-size window and all three chart types, using synthetic data. These are rendered application controls, not design mockups.

Reproduce with `pwsh -NoProfile -File scripts/verify-windows.ps1 -WithPresentationOracle` (openpyxl is an optional preinstalled development reader) and `bash scripts/verify-linux.sh`. The default product/build has no added third-party dependency.

Scope: exact requested native functions; bounded style/layout/theme/table/chart import/export; coordinate-preserving table view sort; staged import and totals. Broader Excel/OOXML fidelity, complete screen-reader/RTL workflows and safe handling of every hostile workbook remain outside these receipts. Table totals/import establish a new native proof chain. Native cell proofs do not cover presentation metadata. Real-user task completion or productivity benefit has not been measured.
