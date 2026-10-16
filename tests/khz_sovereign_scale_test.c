#include "khz_sheet.h"

#include <inttypes.h>
#include <stdint.h>
#include <stdio.h>

/*
 * The requested 100,000 x 1,000 coordinate envelope is 100,000,000
 * addressable positions, NOT 100,000,000 materialized cells.
 *
 * Exercise 100,000 populated cells spread over every target row and
 * every target column. This tests the real C11 sparse grid and its
 * immutable, arena-bounded cell capacity without any XLSX/GUI/network.
 *
 * Cell values are synthetic. This is not a renderer FPS measurement.
 */
#define TARGET_ROWS 100000u
#define TARGET_COLUMNS 1000u
#define TARGET_CELLS ((size_t)TARGET_ROWS)
#define RESERVATION_BYTES ((size_t)128 * (size_t)1024 * (size_t)1024)

#define REQUIRE(expr) do { \
    if (!(expr)) { \
        fprintf(stderr, "FAIL: %s at line %d\n", #expr, __LINE__); \
        goto fail; \
    } \
} while (0)

int main(void)
{
    KhzSheet sheet = {0};
    KhzChainAudit audit = {0};
    const KhzCell *cell = NULL;
    uint32_t row;
    size_t sample;

    REQUIRE(KHZ_GRID_MAX_ROWS >= TARGET_ROWS);
    REQUIRE(KHZ_GRID_MAX_COLUMNS >= TARGET_COLUMNS);
    REQUIRE(khz_sheet_init(&sheet, RESERVATION_BYTES, TARGET_CELLS)
            == KHZ_SHEET_OK);

    for (row = 0u; row < TARGET_ROWS; ++row) {
        const uint32_t column = row % TARGET_COLUMNS;
        REQUIRE(khz_sheet_set_i64(&sheet, column, row,
                                  (int64_t)row + INT64_C(1))
                == KHZ_SHEET_OK);
    }

    REQUIRE(khz_sheet_cell_count(&sheet) == TARGET_CELLS);
    REQUIRE(khz_sheet_commits(&sheet) == (uint64_t)TARGET_CELLS);
    REQUIRE(khz_grid_rejections(&sheet.grid) == UINT64_C(0));
    REQUIRE(khz_arena_used(&sheet.arena) <= RESERVATION_BYTES);

    /* Deliberately sample the beginning, middle and last row. */
    for (sample = 0u; sample < 4u; ++sample) {
        const uint32_t selected_row =
            sample == 0u ? 0u : sample == 1u ? 999u :
            sample == 2u ? 50000u : TARGET_ROWS - 1u;
        const uint32_t selected_col = selected_row % TARGET_COLUMNS;
        REQUIRE(khz_sheet_get(&sheet, selected_col, selected_row,
                              &cell) == KHZ_SHEET_OK);
        REQUIRE(cell != NULL);
        REQUIRE(cell->kind == KHZ_CELL_RATIONAL);
        REQUIRE(cell->value.num == (int64_t)selected_row + INT64_C(1));
        REQUIRE(cell->value.den == INT64_C(1));
    }
    REQUIRE(khz_sheet_get(&sheet, 999u, 0u, &cell)
            == KHZ_SHEET_ERR_MISSING);

    /* Exceeding the declared number of populated cells must be fail-closed,
     * not a hidden realloc that invalidates dependent stable indices. */
    REQUIRE(khz_sheet_set_i64(&sheet, 0u, TARGET_ROWS, INT64_C(777))
            == KHZ_SHEET_ERR_LIMIT);
    REQUIRE(khz_sheet_cell_count(&sheet) == TARGET_CELLS);
    REQUIRE(khz_sheet_commits(&sheet) == (uint64_t)TARGET_CELLS);
    REQUIRE(khz_grid_rejections(&sheet.grid) == UINT64_C(1));

    REQUIRE(khz_sheet_set_i64(&sheet, KHZ_GRID_MAX_COLUMNS, 0u,
                              INT64_C(777)) == KHZ_SHEET_ERR_LIMIT);
    REQUIRE(khz_sheet_set_i64(&sheet, 0u, KHZ_GRID_MAX_ROWS,
                              INT64_C(777)) == KHZ_SHEET_ERR_LIMIT);
    REQUIRE(khz_sheet_cell_count(&sheet) == TARGET_CELLS);
    REQUIRE(khz_sheet_commits(&sheet) == (uint64_t)TARGET_CELLS);

    REQUIRE(khz_sheet_audit_chain(&sheet, &audit) == KHZ_SHEET_OK);
    REQUIRE(audit.entries_examined > UINT64_C(0));
    REQUIRE(audit.dropped > UINT64_C(0));
    REQUIRE(audit.entries_examined + audit.dropped
            == (uint64_t)TARGET_CELLS);

    printf("PASS rows=%u columns=%u populated_cells=%zu "
           "virtual_positions=%" PRIu64 " arena_reserved_bytes=%zu "
           "arena_used_bytes=%zu "
           "proof_entries_examined=%" PRIu64 " proof_dropped=%" PRIu64
           " rejected_extra_cell=%" PRIu64 "\n",
           TARGET_ROWS, TARGET_COLUMNS, TARGET_CELLS,
           (uint64_t)TARGET_ROWS * (uint64_t)TARGET_COLUMNS,
           RESERVATION_BYTES,
           khz_arena_used(&sheet.arena),
           audit.entries_examined, audit.dropped,
           khz_grid_rejections(&sheet.grid));
    khz_sheet_destroy(&sheet);
    return 0;

fail:
    khz_sheet_destroy(&sheet);
    return 1;
}
