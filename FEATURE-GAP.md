# Spreadsheet feature gap

This file is a parity map, not a compatibility claim. Targets were compared against current Microsoft Excel and Google Sheets help for common desktop/web spreadsheet workflows.

## Verified or implemented in KHZ Sheet
- Cell editing through the WPF grid and formula bar.
- Multiple local worksheet tabs.
- CSV open/save and bounded XLSX export.
- Native recalculation and proof verification/copy.
- Clipboard paste and delete/clear.
- Single-cell semantic Undo/Redo through the normal native commit path.
- Case-insensitive Find Next over stored cell inputs/formula source.
- Toggle freeze of the first data column.

## High-value gaps
- XLSX open in the desktop: native reader exists, but no managed reader binding currently exposes it safely.
- Find/Replace: Find Next exists; replacement UI and grouped Replace All history are absent.
- Sort/filter: absent. A naive DataView sort would break visual-row to native-coordinate identity, so it must not be enabled without a coordinate-preserving view model.
- Row/column insert, delete, hide, group and resize persistence.
- Sheet rename/reorder/delete and workbook-level file format.
- Formatting: number formats, fonts, fills, borders, alignment, wrapping and merge.
- Data validation/drop-downs and conditional formatting.
- Tables, charts, pivots, comments/notes and hyperlinks.
- Broader formula families: logical, lookup/reference, text, date/time, statistical and array functions.
- Print/page layout, import/export fidelity and general Excel workbook compatibility.
- Autosave, version history, collaboration, protected ranges and sharing are not local-engine features today.
- Complete keyboard/accessibility, high-contrast and Arabic/RTL workflow verification.

## External feature references inspected
- Microsoft Excel: Sort data in a range or table; Find or replace text and numbers; Excel functions by category.
- Google Sheets: Sort & filter your data; Search and use find and replace; keyboard shortcuts; Sheets cheat sheet.

## Design gate
A feature is not marked implemented because a button exists. It must preserve native cell identity, build cleanly, and have an executable regression or interaction check appropriate to the behavior.
