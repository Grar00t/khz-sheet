# Table support

`TableDefinition` is a managed workbook object: a name, rectangle, unique column labels and optional totals row. `WorksheetSession` validates names, bounds, header edits and overlap. Up to 128 tables are supported in the desktop's 256-row by 52-column viewport. Header and alternating body fills follow the worksheet theme; explicit cell formatting takes precedence.

Select headers plus data and use **Create Table**, or **Table + Totals** to append a summary into an empty row below. Totals use native `SUM` formulas, with a `Total` label in the first column of a multicolumn table. Edits to the data recalculate totals through the native DAG. Totals cannot be overwritten directly. Occupied cells, overlapping tables and duplicate headers are refused. Table creation, totals and their cell changes undo/redo as one action.

Automatic totals stage the complete input set in a candidate native sheet and publish it only after successful evaluation. This operation starts a new native proof chain for that sheet; it does not preserve the old chain as a continuous audit history. Ordinary cell edits continue through the existing native commit path. Import similarly establishes a new sheet/proof history.

**Sort ↑/↓** creates a temporary view over existing `DataRowView` identities, compares rational values by exact integer cross-products, orders numbers before text in ascending order, and breaks equal-key ties by original row number. Headers and totals remain fixed. WPF receives the original DataTable property descriptors so sorted cells remain editable. Editing a displayed row uses its native coordinate, never its display index. **Original Order** restores the view. XLSX export uses coordinate order; it does not materialize the view sort.

XLSX writes `xl/tables/tableN.xml`, relationships, content types, table columns, SUM totals metadata and table style information. The bounded desktop importer verifies table ranges and headers against the imported cells. Native `khz_grid.c` and `khz_cell.c` remain cell/DAG engines; table metadata is not part of their ABI or proof coverage.

Tests: `KhzSubsystemContracts` checks overlap, header consistency, totals calculation and grouped history, exact sort ordering, unchanged native proof during sort, edits after sorting, and XLSX table round trips. Filters, structured references, calculated-column propagation, transactional resize, multi-key/materialized sorting and native table APIs remain outside this implementation.
