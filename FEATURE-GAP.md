# Spreadsheet feature gap

This file is a parity map, not a compatibility claim. Targets were compared against current Microsoft Excel and Google Sheets help for common desktop/web spreadsheet workflows.

## Verified or implemented in KHZ Sheet
- Cell editing through the WPF grid and formula bar.
- Multiple local worksheet tabs.
- CSV open/save and bounded single-sheet XLSX style/layout/table/chart import and export. XLSX export explicitly warns that only the active tab is saved.
- Native recalculation and proof verification/copy.
- Rectangular text clipboard copy/paste and delete/clear.
- Single-cell semantic Undo/Redo through the normal native commit path.
- Case-insensitive Find Next over stored cell inputs/formula source.
- Toggle freeze of the first data column.
- Desktop cell formatting: installed font families, preset sizes, bold/italic/underline, text/fill colors, alignment, wrapping, built-in number formats and borders.
- Explicit formatting and visual table banding serialize to XLSX through `styles.xml` and per-cell style indexes; CSV remains unformatted.
- Visual styling plus semantic table ranges/headers/totals, three chart types and their bounded Open XML parts; see TABLES.md and CHARTS.md.
- Undo/Redo includes cell-format and visual-table-style actions.
- Sheet rename with unique-name validation, confirmed deletion, duplication, and tab reordering.
- Unsaved-change prompts and debounced/periodic recovery snapshots using a temporary generation and published manifest. Per-sheet XLSX recovery retains supported presentation when the native engine is available; engine-unavailable recovery is text-only CSV.
- Per-cell styling on realization, drag-completed column-width persistence, explicit row-height editing, and selection header styling with throttled updates.
- Type-to-edit (WPF), F2, Enter/Tab/Shift+Tab and arrow navigation, Home/End/Ctrl+Arrow, Ctrl+D/Ctrl+R, Name Box jump, and selection Sum/Average/Count display.
- Bounded row/column insertion/deletion rebuilds the native session and adjusts unqualified references. Deleting a directly referenced cell or an entire referenced range yields `#REF!`; surviving ranges contract when an endpoint is removed. Structural changes clear undo history.
- Rejected edits display a banner; spreadsheet error cells are styled distinctly.

## High-value gaps and boundaries
- General/multi-sheet XLSX open: only the canonical single-sheet presentation subset is imported.
- Find/Replace: Find Next exists; replacement UI and grouped Replace All history are absent.
- Filters and persistent/materialized or multi-key sorting: absent. Single-key semantic-table view sorting preserves native coordinates and has an actual sorted-edit regression.
- Hide/group rows and columns are absent. Insert/delete inside semantic table columns is refused; structural changes clear undo history and are not fully Excel-compatible.
- Formula fill repeats source text without relative-reference translation. Clipboard transfer is text-only; formatting is not copied.
- Column auto-fit uses WPF sizing and does not promise Excel font-metric parity. Selection statistics are display-only floating-point summaries; they do not modify stored exact rationals.
- Long-lived native edit churn: failed formula transactions rewind their arena bytes, but retired allocations from successful text/formula replacements remain reserved until the sheet is destroyed; there is no compaction/GC path yet.
- Workbook-level multi-sheet XLSX serialization remains absent.
- Formatting gaps: strikethrough, merge and conditional formatting. Supported explicit RGB styles and known number formats now import and export.
- Data validation/drop-downs and conditional formatting.
- Structured references, table filters/resize/calculated columns, richer or multi-series charts, pivots, comments/notes and hyperlinks.
- Broader formula families: logical, lookup/reference, text, date/time, statistical and array functions.
- Print/page layout, import/export fidelity and general Excel workbook compatibility.
- Recovery snapshots are not a named workbook save or version history. Text-only CSV recovery can be restored without the engine; numerical/formula recovery requires the native engine. Collaboration, protected ranges and sharing are not local-engine features today.
- Complete keyboard/accessibility, high-contrast and Arabic/RTL workflow verification.
- WPF interaction assertions are present in `KhzSubsystemContracts`, but must be run by `pwsh scripts/verify-windows.ps1` on Windows; a cross-target build on another OS proves compilation only.

## External feature references inspected
- Microsoft Excel: Sort data in a range or table; Find or replace text and numbers; Excel functions by category.
- Google Sheets: Sort & filter your data; Search and use find and replace; keyboard shortcuts; Sheets cheat sheet.

## Design gate
A feature is not marked implemented because a button exists. It must preserve native cell identity, build cleanly, and have an executable regression or interaction check appropriate to the behavior.
