# Changes from f19d09f067f35380830a5128e1996391faae68b9
- Guard NULL formula constructor output pointers before allocation; bound unary-plus recursion.
- Preserve representable exact addition/subtraction and scaled sums when only an intermediate overflows. Preserve scalar/SIMD final-sum equivalence through a private C11 two-limb fallback.
- Roll back newly declared native dependency cells/edges/flags on failure. Bound public dependency walking. Restore a formula cell after a failed recalculation commit.
- Reject ambiguous ZIP local metadata, duplicate names, overlapping extents and unsupported flags; validate data descriptors; enforce archive/aggregate/ratio ceilings.
- Isolate managed scratch IR with SafeHandle ownership; serialize ABI verification publication and translate malformed-library image failures.
- Add native boundary/property tests, a development-only independent rational oracle and managed boundary regressions.
- Add KHZ Sheet desktop branding, proof-copy, Undo/Redo, Find Next, first-column freeze, cell typography/colors/alignment/borders, visual table styling, and optional ML-KEM-768 / ML-DSA-65 hybrid encrypted release packaging with local-only secret keys.
Compatibility: ABI 96 and public layouts are unchanged. Some operations formerly returning ERR_OVERFLOW now succeed exactly. Archive acceptance is intentionally narrower; ceilings are compile-time configurable. Existing callers relying on ambiguous ZIPs must regenerate valid packages, not disable checks.
No new formula functions, semantic table/chart model or localization subsystem was implemented. Desktop formatting is session-only and is not serialized into CSV/XLSX. Sort/filter, XLSX open, replacement workflows and richer workbook operations are still absent. Managed lifetime/ABI corrections are present in the reviewed tree and their regressions pass.
