#include "khz_abi.h"

#include <stddef.h>
#include <string.h>

#include "khz_grid.h"
#include "khz_ledger.h"
#include "khz_sheet.h"
#include "khz_simd.h"

KhzSheetStatus khz_abi_sizes(KhzAbiSizes *out)
{
    if (out == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    memset(out, 0, sizeof *out);

    out->version = KHZ_ABI_VERSION;
    out->pointer_bytes = (uint32_t)sizeof(void *);
    out->size_t_bytes = (uint32_t)sizeof(size_t);
    out->arena_bytes = (uint32_t)sizeof(KhzArena);
    out->rational_bytes = (uint32_t)sizeof(KhzRational);
    out->cell_bytes = (uint32_t)sizeof(KhzCell);
    out->grid_slot_bytes = (uint32_t)sizeof(KhzGridSlot);
    out->grid_bytes = (uint32_t)sizeof(KhzGrid);
    out->dep_graph_bytes = (uint32_t)sizeof(KhzDepGraph);
    out->sheet_bytes = (uint32_t)sizeof(KhzSheet);
    out->ledger_bytes = (uint32_t)sizeof(KhzLedger);
    out->cell_proof_offset = (uint32_t)offsetof(KhzCell, proof);
    out->cell_value_offset = (uint32_t)offsetof(KhzCell, value);
    out->sha256_digest_bytes = (uint32_t)KHZ_SHA256_DIGEST_BYTES;

    return KHZ_SHEET_OK;
}

uint32_t khz_abi_version(void)
{
    return KHZ_ABI_VERSION;
}

int khz_abi_simd_compiled(void)
{
    return khz_simd_kernel() == KHZ_SIMD_SCALAR ? 0 : 1;
}

int khz_abi_ledger_compiled(void)
{
#ifdef KHZ_WITH_SQLITE
    return 1;
#else
    return 0;
#endif
}
