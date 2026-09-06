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
   turned into a status code instead. */
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

/* 1 when the library was built with the AVX2 or NEON kernel compiled in, 0
   for the scalar build. Reported rather than inferred from the host CPU: what
   matters is what was compiled, not what the machine could have run. */
int khz_abi_simd_compiled(void);

/* 1 when this build contains the SQLite ledger, 0 when it was configured
   out. A managed caller must not call the ledger entry points when this
   returns 0. */
int khz_abi_ledger_compiled(void);

#ifdef __cplusplus
}
#endif

#endif /* KHZ_ABI_H */
