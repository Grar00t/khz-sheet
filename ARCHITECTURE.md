# Architecture
The authoritative engine is C11. `khz_rational.c` normalizes and checks exact values; `khz_wide.h` supplies private two-limb intermediates. It does not change the public numeric representation.
`khz_formula.c` parses/builds native IR. `khz_formula_eval.c` evaluates it. `khz_formula_deps.c` installs formulas, tracks dependencies and performs filtered topological recalculation. `khz_grid.c` owns sparse-coordinate indexing and arena-backed cells/edges.
`khz_cell.c`, `khz_hash.c` and `khz_sheet.c` implement revisioned cell proofs and commit history. `khz_ledger.c` optionally persists proof metadata in system SQLite.
`khz_xlsx.c` writes a narrow XLSX package. `khz_xlsx_reader.c` validates ZIP framing and decodes members through `khz_inflate.c`; `khz_xlsx_reader_parse.c` implements the supported XML subset.
`KHZ.Sheet.Core` provides P/Invoke, managed parsing and OPC editing. It must not become a second arithmetic engine. `KHZ.Sheet.Desktop` is the Windows WPF client.
No table engine, chart model, chart renderer, localization-resource subsystem or approximate numeric cell kind is implemented. A DataTable used by the desktop is not a workbook table model.
The native ABI remains version 96. No public structure layout was changed by this intervention.
