# Threat model
Inputs: formula bytes, caller-supplied native pointers/lengths, archive members/XML, managed ASTs, native DLL selection and SQLite history. Assets: process memory, bounded resources, unchanged state on rejected edits, exact values and proof continuity.
Addressed: null output writes in formula builders; unary-plus depth bypass; false arithmetic overflow before reduction; SIMD ordering-dependent overflow; failed native formula mutation/recalculation state; ambiguous ZIP metadata, duplicate names and overlapping extents.
Also addressed in the final managed source: AST edge lifetime, concurrent first ABI verification and malformed-DLL exception handling.
Remaining: complete XML namespace/depth/token enforcement, shared-string amplification, runtime DLL search hardening, full dependency mutation fault injection and whole-workbook transactionality.
Assumptions: callers supply valid allocated buffers and serialize sheet access; system/compiler/SQLite binaries are trusted. The native C API cannot validate arbitrary forged pointers. Attacker control of the entire proof history is outside unkeyed-chain authenticity guarantees.
