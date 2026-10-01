# Local-first formula and worksheet improvements (2026-10-01)

- Add exact AND/OR/NOT/IFERROR formulas, lazy IFERROR fallback evaluation, validated worksheet lifecycle actions, and row/column/combined frozen-pane XLSX round trips. Organize desktop commands into Home, Insert / Data and View tabs; WPF row freezing remains unavailable.

## Windows and subsystem enhancement (2026-09-30)
- Enable/probe MSVC C11 atomics and Windows LEDGER=AUTO fallback; default strict native/managed warnings and scoped compiler TEMP handling in verification.
- Add exact COUNT/COUNTA, PRODUCT, ABS, ROUND, CEILING, FLOOR, lazy IF and comparisons. Align managed range/function lowering and bound managed parsing; reject literals without exact source text.
- Round-trip explicit RGB styles, independent borders, supported custom formats, dimensions, frozen columns and four themes through a bounded, staged desktop XLSX importer.
- Add semantic tables, exact native SUM totals with grouped history, header validation and editable coordinate-preserving sort views. Add Bar/Column/Line models, live WPF charts, accessible exact data descriptions and Open XML drawing/chart parts.
- Bound OPC memory/expansion and XML nesting/tokens, refuse DTDs and unsupported import structures, and expand malformed workbook regressions.
- Add reproducible Windows/Linux verification scripts and subsystem interaction/round-trip tests. Current evidence is recorded separately from claims of complete Excel compatibility or measured user benefit.

## Earlier changes from f19d09f067f35380830a5128e1996391faae68b9
- Guard NULL formula constructor output pointers before allocation; bound unary-plus recursion.
- Preserve representable exact addition/subtraction and scaled sums when only an intermediate overflows. Preserve scalar/SIMD final-sum equivalence through a private C11 two-limb fallback.
- Roll back newly declared native dependency cells/edges/flags on failure. Bound public dependency walking. Restore a formula cell after a failed recalculation commit.
- Reject ambiguous ZIP local metadata, duplicate names, overlapping extents and unsupported flags; validate data descriptors; enforce archive/aggregate/ratio ceilings.
- Isolate managed scratch IR with SafeHandle ownership; serialize ABI verification publication and translate malformed-library image failures.
- Add native boundary/property tests, a development-only independent rational oracle and managed boundary regressions.
- Add KHZ Sheet desktop branding, proof-copy, Undo/Redo, Find Next, first-column freeze, cell typography/colors/alignment/borders, underline/wrap, bounded built-in number formats, visual table styling, XLSX style serialization, and optional ML-KEM-768 / ML-DSA-65 hybrid encrypted release packaging with local-only secret keys.
Compatibility: ABI 96 and public layouts are unchanged. Some operations formerly returning ERR_OVERFLOW now succeed exactly. Archive acceptance is intentionally narrower; ceilings are compile-time configurable. Existing callers relying on ambiguous ZIPs must regenerate valid packages, not disable checks.
No new formula functions, semantic table/chart model or localization subsystem was implemented. Desktop formatting now includes underline, wrapping and built-in number formats and serializes the implemented presentation subset into XLSX via `styles.xml`; CSV remains value-only. Sort/filter, managed XLSX open/style import, replacement workflows and richer workbook operations are still absent. Managed lifetime/ABI corrections are unchanged and their regressions remain part of the release gate.
