#include "khz_formula.h"
#include "khz_sheet.h"
#include <stdio.h>
#include <string.h>
#define CHECK(c) do { ++checks; if (!(c)) { \
    fprintf(stderr, "FAIL line %d: %s\n", __LINE__, #c); ++failures; } } while (0)
int main(void)
{
    KhzSheet sheet;
    const KhzCell *cell = NULL;
    KhzCell before;
    int checks = 0, failures = 0;
    uint64_t evaluated, commits;
    size_t arena_used;
    if (khz_sheet_init(&sheet, (size_t)1 << 20, 4) != KHZ_SHEET_OK) return 2;
    CHECK(khz_sheet_set_i64(&sheet, 0, 0, 1) == KHZ_SHEET_OK);
    CHECK(khz_formula_set(&sheet, 1, 0, "1", 1, NULL) == KHZ_SHEET_OK);
    CHECK(khz_formula_recalc(&sheet, &evaluated) == KHZ_SHEET_OK && evaluated == 1);
    CHECK(khz_sheet_get(&sheet, 1, 0, &cell) == KHZ_SHEET_OK);
    if (cell == NULL) return 2;
    before = *cell;
    commits = sheet.commits;
    arena_used = khz_arena_used(&sheet.arena);
    const char *bad = "SUM(A1:A1)+C1+D1+E1";
    CHECK(khz_formula_set(&sheet, 1, 0, bad, strlen(bad), NULL) == KHZ_SHEET_ERR_LIMIT);
    CHECK(khz_sheet_cell_count(&sheet) == 2);
    CHECK(memcmp(&before, cell, sizeof before) == 0);
    CHECK(khz_arena_used(&sheet.arena) == arena_used);
    CHECK(sheet.commits == commits && sheet.deps.edge_count == 0);
    CHECK(sheet.deps.range_count == 0 && sheet.deps.range_links == 0);
    CHECK(khz_sheet_get(&sheet, 2, 0, &cell) == KHZ_SHEET_ERR_MISSING);
    CHECK(khz_sheet_get(&sheet, 3, 0, &cell) == KHZ_SHEET_ERR_MISSING);
    CHECK(khz_sheet_set_i64(&sheet, 0, 0, 2) == KHZ_SHEET_OK);
    CHECK(khz_formula_recalc(&sheet, &evaluated) == KHZ_SHEET_OK && evaluated == 0);
    CHECK(khz_sheet_verify_chain(&sheet, NULL) == KHZ_SHEET_OK);
    khz_sheet_destroy(&sheet);
    if (khz_sheet_init(&sheet, (size_t)1 << 20, 8) != KHZ_SHEET_OK) return 2;
    CHECK(khz_sheet_set_i64(&sheet, 0, 0, 1) == KHZ_SHEET_OK);
    CHECK(khz_formula_set(&sheet, 1, 0, "A1+1", 4, NULL) == KHZ_SHEET_OK);
    CHECK(khz_formula_recalc(&sheet, &evaluated) == KHZ_SHEET_OK);
    CHECK(khz_sheet_set_i64(&sheet, 0, 0, 4) == KHZ_SHEET_OK);
    CHECK(khz_sheet_get(&sheet, 1, 0, &cell) == KHZ_SHEET_OK);
    if (cell == NULL) return 2;
    before = *cell;
    commits = sheet.commits;
    sheet.commits = UINT64_MAX;
    CHECK(khz_formula_recalc(&sheet, &evaluated) == KHZ_SHEET_ERR_OVERFLOW);
    CHECK(memcmp(&before, cell, sizeof before) == 0);
    sheet.commits = commits;
    CHECK(khz_sheet_verify_chain(&sheet, NULL) == KHZ_SHEET_OK);
    CHECK(khz_formula_recalc(&sheet, &evaluated) == KHZ_SHEET_OK && evaluated == 1);
    CHECK(cell->value.num == 5 && cell->value.den == 1);
    CHECK(khz_sheet_verify_chain(&sheet, NULL) == KHZ_SHEET_OK);
    khz_sheet_destroy(&sheet);
    printf("checks=%d failures=%d\n", checks, failures);
    return failures ? 1 : 0;
}
