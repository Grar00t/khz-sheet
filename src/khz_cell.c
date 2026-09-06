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
        return KHZ_SH