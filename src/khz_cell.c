#include "khz_cell.h"

#include <string.h>

#include "khz_grid.h"

static void khz_store_le32(unsigned char *p, uint32_t value)
{
    unsigned int i;
    for (i = 0u; i < 4u; ++i) {
        p[i] = (unsigned char)((value >> (8u * i)) & 0xffu);
    }
}

static void khz_store_le64(unsigned char *p, uint64_t value)
{
    unsigned int i;
    for (i = 0u; i < 8u; ++i) {
        p[i] = (unsigned char)((value >> (8u * i)) & 0xffu);
    }
}

uint64_t khz_cell_key(uint32_t col, uint32_t row)
{
    return ((uint64_t)col << 32) | (uint64_t)row;
}

KhzSheetStatus khz_cell_key_split(uint64_t key, uint32_t *col, uint32_t *row)
{
    if (col == NULL || row == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    *col = (uint32_t)(key >> 32);
    *row = (uint32_t)(key & 0xffffffffull);
    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_cell_init_empty(KhzCell *cell, uint32_t col, uint32_t row)
{
    if (cell == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }
    if (col >= KHZ_GRID_MAX_COLUMNS || row >= KHZ_GRID_MAX_ROWS) {
        return KHZ_SHEET_ERR_LIMIT;
    }

    memset(cell, 0, sizeof *cell);
    cell->col = col;
    cell->row = row;
    cell->kind = (uint32_t)KHZ_CELL_EMPTY;
    cell->value = khz_rational_zero();
    return KHZ_SHEET_OK;
}

/* Clears every payload field so no kind can inherit another's data. Coordinate,
   revision, proof and flags survive: they describe the cell, not its value. */
static void khz_cell_clear_payload(KhzCell *cell)
{
    cell->value = khz_rational_zero();
    cell->text = NULL;
    cell->formula = NULL;
    cell->text_len = 0u;
    cell->formula_len = 0u;
    cell->bool_value = 0u;
    cell->error = (uint32_t)KHZ_CELL_ERROR_NONE;
}

KhzSheetStatus khz_cell_set_empty(KhzCell *cell)
{
    if (cell == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    khz_cell_clear_payload(cell);
    cell->kind = (uint32_t)KHZ_CELL_EMPTY;
    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_cell_set_rational(KhzCell *cell, KhzRational value)
{
    if (cell == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }
    if (!khz_rational_is_valid(value)) {
        return KHZ_SHEET_ERR_RANGE;
    }

    khz_cell_clear_payload(cell);
    cell->kind = (uint32_t)KHZ_CELL_RATIONAL;
    cell->value = value;
    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_cell_set_text(KhzCell *cell, const char *text, uint32_t len)
{
    if (cell == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }
    if (text == NULL && len != 0u) {
        return KHZ_SHEET_ERR_NULL;
    }

    khz_cell_clear_payload(cell);
    cell->kind = (uint32_t)KHZ_CELL_TEXT;
    cell->text = text;
    cell->text_len = len;
    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_cell_set_bool(KhzCell *cell, int value)
{
    if (cell == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    khz_cell_clear_payload(cell);
    cell->kind = (uint32_t)KHZ_CELL_BOOL;
    cell->bool_value = value != 0 ? 1u : 0u;
    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_cell_set_error(KhzCell *cell, KhzCellError error)
{
    if (cell == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }
    if (error < KHZ_CELL_ERROR_NULL || error > KHZ_CELL_ERROR_NA) {
        return KHZ_SHEET_ERR_RANGE;
    }

    khz_cell_clear_payload(cell);
    cell->kind = (uint32_t)KHZ_CELL_ERROR;
    cell->error = (uint32_t)error;
    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_cell_set_formula(KhzCell *cell, const char *formula, uint32_t len)
{
    if (cell == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }
    if (formula == NULL && len != 0u) {
        return KHZ_SHEET_ERR_NULL;
    }

    khz_cell_clear_payload(cell);
    cell->kind = (uint32_t)KHZ_CELL_FORMULA;
    cell->formula = formula;
    cell->formula_len = len;

    cell->flags |= KHZ_CELL_FLAG_DIRTY;
    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_cell_digest(const KhzCell *cell,
                               const unsigned char prev[KHZ_SHA256_DIGEST_BYTES],
                               unsigned char out[KHZ_SHA256_DIGEST_BYTES])
{
    KhzSha256 ctx;
    KhzHashStatus hash;
    unsigned char header[64];
    uint32_t proof_flags;
    size_t at = (size_t)0;

    if (cell == NULL || prev == NULL || out == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    /* Only flags that describe committed semantic state enter the digest.
       DIRTY is scheduler/cache state: dependency invalidation may set it and
       recalculation may clear it without either transition being a cell edit.
       Hashing it would make ordinary invalidation look like tampering. */
    proof_flags = cell->flags & KHZ_CELL_PROOF_FLAGS_MASK;

    khz_store_le32(header + at, cell->col);            at += 4u;
    khz_store_le32(header + at, cell->row);            at += 4u;
    khz_store_le32(header + at, cell->kind);           at += 4u;
    khz_store_le32(header + at, cell->error);          at += 4u;
    khz_store_le64(header + at, (uint64_t)cell->value.num); at += 8u;
    khz_store_le64(header + at, (uint64_t)cell->value.den); at += 8u;
    khz_store_le32(header + at, cell->text_len);       at += 4u;
    khz_store_le32(header + at, cell->formula_len);    at += 4u;
    khz_store_le32(header + at, cell->bool_value);     at += 4u;
    khz_store_le32(header + at, proof_flags);          at += 4u;
    khz_store_le64(header + at, cell->revision);       at += 8u;

    hash = khz_sha256_init(&ctx);
    if (hash != KHZ_HASH_OK) {
        return KHZ_SHEET_ERR_STATE;
    }

    if (khz_sha256_update(&ctx, prev, (size_t)KHZ_SHA256_DIGEST_BYTES) != KHZ_HASH_OK) {
        return KHZ_SHEET_ERR_STATE;
    }
    if (khz_sha256_update(&ctx, KHZ_CELL_PROOF_TAG, KHZ_CELL_PROOF_TAG_BYTES) != KHZ_HASH_OK) {
        return KHZ_SHEET_ERR_STATE;
    }
    if (khz_sha256_update(&ctx, header, at) != KHZ_HASH_OK) {
        return KHZ_SHEET_ERR_STATE;
    }

    if (cell->text_len != 0u) {
        if (cell->text == NULL) {
            return KHZ_SHEET_ERR_STATE;
        }
        if (khz_sha256_update(&ctx, cell->text, (size_t)cell->text_len) != KHZ_HASH_OK) {
            return KHZ_SHEET_ERR_STATE;
        }
    }

    if (cell->formula_len != 0u) {
        if (cell->formula == NULL) {
            return KHZ_SHEET_ERR_STATE;
        }
        if (khz_sha256_update(&ctx, cell->formula, (size_t)cell->formula_len) != KHZ_HASH_OK) {
            return KHZ_SHEET_ERR_STATE;
        }
    }

    if (khz_sha256_final(&ctx, out) != KHZ_HASH_OK) {
        return KHZ_SHEET_ERR_STATE;
    }

    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_cell_commit(KhzCell *cell,
                               const unsigned char prev[KHZ_SHA256_DIGEST_BYTES])
{
    unsigned char digest[KHZ_SHA256_DIGEST_BYTES];
    KhzSheetStatus status;

    if (cell == NULL || prev == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    if (cell->revision == UINT64_MAX) {
        return KHZ_SHEET_ERR_OVERFLOW;
    }

    cell->revision += (uint64_t)1;

    status = khz_cell_digest(cell, prev, digest);
    if (status != KHZ_SHEET_OK) {
        cell->revision -= (uint64_t)1;
        return status;
    }

    memcpy(cell->proof, digest, sizeof digest);
    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_cell_verify(const KhzCell *cell,
                               const unsigned char prev[KHZ_SHA256_DIGEST_BYTES])
{
    unsigned char digest[KHZ_SHA256_DIGEST_BYTES];
    KhzSheetStatus status;

    if (cell == NULL || prev == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    if (cell->revision == (uint64_t)0) {
        return KHZ_SHEET_ERR_STATE;
    }

    status = khz_cell_digest(cell, prev, digest);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    if (khz_hash_equal_ct(digest, cell->proof, (size_t)KHZ_SHA256_DIGEST_BYTES) == 0) {
        return KHZ_SHEET_ERR_FORMAT;
    }

    return KHZ_SHEET_OK;
}

const char *khz_cell_kind_name(KhzCellKind kind)
{
    switch (kind) {
        case KHZ_CELL_EMPTY:
            return "EMPTY";
        case KHZ_CELL_RATIONAL:
            return "RATIONAL";
        case KHZ_CELL_TEXT:
            return "TEXT";
        case KHZ_CELL_BOOL:
            return "BOOL";
        case KHZ_CELL_ERROR:
            return "ERROR";
        case KHZ_CELL_FORMULA:
            return "FORMULA";
        default:
            return "UNKNOWN";
    }
}

const char *khz_cell_error_name(KhzCellError error)
{
    switch (error) {
        case KHZ_CELL_ERROR_NONE:
            return "";
        case KHZ_CELL_ERROR_NULL:
            return "#NULL!";
        case KHZ_CELL_ERROR_DIV0:
            return "#DIV/0!";
        case KHZ_CELL_ERROR_VALUE:
            return "#VALUE!";
        case KHZ_CELL_ERROR_REF:
            return "#REF!";
        case KHZ_CELL_ERROR_NAME:
            return "#NAME?";
        case KHZ_CELL_ERROR_NUM:
            return "#NUM!";
        case KHZ_CELL_ERROR_NA:
            return "#N/A";
        default:
            return "#UNKNOWN";
    }
}
