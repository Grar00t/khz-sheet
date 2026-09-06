#ifndef KHZ_ABI_H
#define KHZ_ABI_H

#include <stddef.h>
#include <stdint.h>

#include "khz_status.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Bumped whenever any struct in the public headers changes layout. A managed
   caller that mirrors these structs has no way to notice a silent layout
   change on its own: it would read the wrong offsets and corrupt memory
   quietly. This header exists so the mismatch is detected at load time and
   turned into a status code instead.

   Held at 91 through Phase 92 and Phase 93 because those phases added
   functions, not struct members. Phase 94 bumped it to 94: KhzSheet gained a
   KhzCommitLog member, which moved proof, commits and initialised and changed
   sizeof(KhzSheet).

   Phase 96 is the same kind of event and is now 95. KhzDepGraph gained three
   members - the declared-region list head, the region count and the count of
   concrete edges materialised from regions - so sizeof(KhzDepGraph) changed.
   KhzDepGraph sits inside KhzSheet ahead of the commit log and the proof, so
   this also moved every KhzSheet member after it. Any caller that hardcoded
   those offsets instead of asking khz_abi_*_offset() is now wrong, which is
   precisely what this constant is for.

   KhzAbiSizes itself remains unchanged in layout. dep_graph_bytes is an
   existing field and simply reports a larger number now; the new range-edge
   query below is a free function, not a new struct member, so a managed
   mirror compiled against 91 still reads the right fields at the right
   offsets and only has to widen the version it accepts. */
#define KHZ_ABI_VERSION ((uint32_t)95)

typedef struct KhzAbiSizes {
    uint32_t version;
    uint32_t pointer_bytes;
    uint32_t size_t_bytes;
    uint32_t arena_bytes;
    uint32_t rational_bytes;
    uint32_t cell_bytes;
    uint32_t grid_slot_bytes;
    uint32_t grid_bytes;
    uint32_t dep_graph_bytes;
    uint32_t sheet_bytes;
    uint32_t ledger_bytes;
    uint32_t cell_proof_offset;
    uint32_t cell_value_offset;
    uint32_t sha256_digest_bytes;
} KhzAbiSizes;

/* Fills *out with the sizes and offsets this build actually compiled. The
   caller compares them against its own mirrored declarations. */
KhzSheetStatus khz_abi_sizes(KhzAbiSizes *out);

uint32_t khz_abi_version(void);

/* Byte offsets of the KhzSheet members, reported by the compiler that built
   this library.

   These exist because the managed bindings need a KhzGrid pointer to call
   khz_grid_count and friends, and Phase 91 shipped no way to obtain one: the
   grid entry points were bound but unreachable. A caller adds the offset to
   its KhzSheet pointer.

   Reported rather than hardcoded on the managed side: the offset of grid
   depends on sizeof(KhzArena), which contains atomics whose size and
   alignment are the compiler's business, not the binding author's. Guessing it
   would produce a pointer that is wrong by a few bytes and corrupt the grid
   silently. Phases 94 and 96 are both proof of that argument - inserting the
   commit log moved proof, growing the dependency graph moved everything after
   it, and every caller that resolves offsets through these functions is
   already correct without being recompiled.

   A C caller does not need any of this - KhzSheet is a complete type in
   khz_sheet.h, so &sheet->grid is already available and is the right way to
   do it there. */
size_t khz_abi_arena_offset(void);
size_t khz_abi_grid_offset(void);
size_t khz_abi_dep_graph_offset(void);
size_t khz_abi_proof_offset(void);
size_t khz_abi_commit_log_offset(void);

/* sizeof(KhzSheet), so a managed caller can allocate a sheet control block
   without mirroring the struct at all. */
size_t khz_abi_sheet_bytes(void);

/* sizeof(KhzDepEdge) and sizeof(KhzDepRangeEdge), reported by the compiler.

   A range edge is strictly larger than a cell edge: it carries a four-field
   rectangle and a scan watermark on top of the target index and the list
   link. The managed side does not allocate either of these - the arena does -
   so these are here for the version gate to assert against rather than for
   allocation. If a future phase changes the rectangle representation without
   bumping KHZ_ABI_VERSION, a gate that checks this number will catch it. */
size_t khz_abi_dep_edge_bytes(void);
size_t khz_abi_dep_range_edge_bytes(void);

/* sizeof(KhzFormula) and sizeof(KhzFormulaNode), reported by the compiler.

   KhzFormula is opaque to the managed side: it holds an arena pointer, a
   root pointer, an arena mark and counters, and the managed lowerer only ever
   passes it back and forth. Phase 93 allocated a fixed 256-byte block for it
   and hoped, which is a guess about pointer size, size_t width and struct
   padding all at once. If the real struct were larger, the builder would
   write past the block and corrupt whatever followed it, and nothing would
   report it. These functions replace the guess with the number the compiler
   used. */
size_t khz_abi_formula_bytes(void);
size_t khz_abi_formula_node_bytes(void);

/* 1 when the library was built with the AVX2 or NEON kernel compiled in, 0
   for the scalar build. Reported rather than inferred from the host CPU: what
   matters is what was compiled, not what the machine could have run. */
int khz_abi_simd_compiled(void);

/* 1 when this build contains the SQLite ledger, 0 when it was configured
   out. A managed caller must not call the ledger entry points when this
   returns 0. */
int khz_abi_ledger_compiled(void);

/* 1 when this build contains the xlsx writer. */
int khz_abi_xlsx_compiled(void);

/* 1 when this build contains the xlsx reader. */
int khz_abi_xlsx_reader_compiled(void);

/* 1 when this build resolves range references as retroactive region edges
   rather than as one edge per cell that existed at declaration time. */
int khz_abi_range_edges_compiled(void);

#ifdef __cplusplus
}
#endif

#endif /* KHZ_ABI_H */
