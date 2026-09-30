# Desktop boundary

The WPF application targets .NET 8 on Windows. `WorksheetSession` maintains a bounded 256-by-52 DataTable view while C11 owns numerical values, evaluation, dependency edges and cell proofs. No third-party UI, chart or Open XML packages are introduced.

Open XLSX stages a canonical single-worksheet package in a separate native session. It imports supported styles, dimensions, frozen columns, preset theme, semantic tables and chart definitions; a rejected package leaves the current session untouched. Unknown parts or unsupported worksheet constructs are refused. Multiple in-memory tabs remain separate exports, not a multi-sheet XLSX workbook.

Formatting includes font family/size, bold/italic/underline, RGB text/fill colors, horizontal and vertical alignment, wrapping, independent thin/medium/double borders and number formats (general, integer, decimal, thousands, percentage, fraction, USD and ISO date). UI formats change display, not the stored rational. Explicit styles serialize to `styles.xml` and worksheet `s` indexes. CSV remains value-only. ISO dates use Excel's 1900 epoch, including its displayed fictitious 1900-02-29.

Column resizing stores character-width units using the UI's fixed pixel conversion; row resizing and the Row Height command store points. Both dimensions round-trip numerically. Rendering does not claim font-metric parity with Excel. Freeze A shows a divider; current selection marks row/column headers. Formula text has a separate colored syntax preview while the editable source remains plain text.

Classic Light, Monochromatic Dark, Corporate Blue and High Contrast update WPF resource colors, table fills and exported theme/styles. Imported explicit RGB styles continue to override a theme. See TABLES.md and CHARTS.md for table totals, coordinate-safe view sorting and live native-backed charts.

`KhzSubsystemContracts` opens the real WPF window, changes themes, sorts and edits a table, rejects a bad edit without changing its displayed value, changes dimensions, renders all chart kinds and saves visual receipts. The release executable also has a separate startup/response/close smoke check. See the current machine receipts rather than inferring success from this description.

Unsaved-change prompts, autosave, grouped rectangular paste, Find/Replace All, row/column insertion/deletion, complete localization and exhaustive accessibility testing remain separate work. Application usability and benefit to real users have not been measured.
