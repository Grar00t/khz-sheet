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

   Still 91 in Phase 92 on purpose. The offset queries added below are new
   functions, not new struct members, so KhzAbiSizes has exactly the layout it
   had in Phase 91 and every binding compiled against 91 remains correct. A
   version bump here would have forced a rewrite of the managed mirror for no
   layout change at all. */
#define KHZ_ABI_VERSION ((uint32_t)91)

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
   silently.

   A C caller does not need any of this - KhzSheet is a complete type in
   khz_sheet.h, so &sheet->grid is already available and is the right way to
   do it there. */
size_t khz_abi_arena_offset(void);
size_t khz_abi_grid_offset(void);
size_t khz_abi_dep_graph_offset(void);
size_t khz_abi_proof_offset(void);

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

#ifdef __cplusplus
}
#endif

#endif /* KHZ_ABI_H */
