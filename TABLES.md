# Table support
There is no versioned native workbook table model in this revision. Stable table/column IDs, structured references, transactional resize, calculated columns, filter state, row-preserving multi-key sort and totals-row semantics are not implemented.
The WPF grid's DataTable is a presentation container, not evidence of spreadsheet table semantics. Table commands, undo, accessible filter UI and RTL placement are absent. Native table XLSX import/export and semantic round-trip tests are absent.
Unsupported table parts may survive particular managed OPC edits, but this is payload preservation rather than table understanding. No table feature is marked VERIFIED.
