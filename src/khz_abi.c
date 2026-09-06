#include "khz_abi.h"

#include <stddef.h>
#include <string.h>

#include "khz_formula.h"
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

size_t khz_abi_arena_offset(void)
{
    return offsetof(KhzSheet, arena);
}

size_t khz_abi_grid_offset(void)
{
    return offsetof(KhzSheet, grid);
}

size_t khz_abi_dep_graph_offset(void)
{
    return offsetof(KhzSheet, deps);
}

size_t khz_abi_proof_offset(void)
{
    return offsetof(KhzSheet, proof);
}

size_t khz_abi_commit_log_offset(void)
{
    return offsetof(KhzSheet, log);
}

size_t khz_abi_sheet_bytes(void)
{
    return sizeof(KhzSheet);
}

/* The numbers the compiler used, not the numbers a binding author guessed.
   Phase 93 assumed 256 bytes for KhzFormula; on an LP64 build the struct is
   two pointers, a size_t, two uint32_t and two uint64_t, and on an ILP32
   build it is smaller again. Either way the managed side has no business
   deriving it, so it asks. */
size_t khz_abi_formula_bytes(void)
{
    return sizeof(KhzFormula);
}

size_t khz_abi_formula_node_bytes(void)
{
    return sizeof(KhzFormulaNode);
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

int khz_abi_xlsx_compiled(void)
{
    return 1;
}

int khz_abi_xlsx_reader_compiled(void)
{
    return 1;
}
