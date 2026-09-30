#include "khz_sheet.h"
#include "khz_formula.h"
#include "khz_rational.h"

#include <stdio.h>
#include <string.h>

#define CHECK(c) do { \
    ++checks; \
    if (!(c)) { \
        fprintf(stderr, "FAIL line %d: %s\n", __LINE__, #c); \
        ++failures; \
    } \
} while (0)

int main(void)
{
    KhzSheet sheet;
    const KhzCell *cell = NULL;
    uint64_t evaluated = 0u;
    int checks = 0;
    int failures = 0;
    KhzFormulaParseError perr;
    KhzRational r5, rneg;

    if (khz_sheet_init(&sheet, (size_t)1 << 20, 32u) != KHZ_SHEET_OK) {
        return 2;
    }

    CHECK(khz_rational_make(5, 1, &r5) == KHZ_SHEET_OK);
    CHECK(khz_rational_make(-7, 2, &rneg) == KHZ_SHEET_OK);
    CHECK(khz_sheet_set_rational(&sheet, 0u, 0u, r5) == KHZ_SHEET_OK);
    CHECK(khz_sheet_set_rational(&sheet, 1u, 0u, rneg) == KHZ_SHEET_OK);

    CHECK(khz_formula_set(&sheet, 0u, 1u, "ABS(A1)", 7u, &perr) == KHZ_SHEET_OK);
    CHECK(khz_formula_set(&sheet, 1u, 1u, "ABS(B1)", 7u, &perr) == KHZ_SHEET_OK);
    CHECK(khz_formula_set(&sheet, 2u, 1u, "IF(A1,10,20)", 12u, &perr) == KHZ_SHEET_OK);
    CHECK(khz_formula_set(&sheet, 3u, 1u, "IF(0,10,20)", 11u, &perr) == KHZ_SHEET_OK);
    CHECK(khz_formula_set(&sheet, 4u, 1u, "IF(B1,ABS(B1),0)", 16u, &perr) == KHZ_SHEET_OK);

    CHECK(khz_formula_recalc(&sheet, &evaluated) == KHZ_SHEET_OK);
    CHECK(evaluated >= 5u);

    CHECK(khz_sheet_get(&sheet, 0u, 1u, &cell) == KHZ_SHEET_OK);
    CHECK(cell != NULL && cell->kind == (uint32_t)KHZ_CELL_FORMULA
          && cell->error == (uint32_t)KHZ_CELL_ERROR_NONE
          && cell->value.num == 5 && cell->value.den == 1);

    CHECK(khz_sheet_get(&sheet, 1u, 1u, &cell) == KHZ_SHEET_OK);
    CHECK(cell != NULL && cell->kind == (uint32_t)KHZ_CELL_FORMULA
          && cell->error == (uint32_t)KHZ_CELL_ERROR_NONE
          && cell->value.num == 7 && cell->value.den == 2);

    CHECK(khz_sheet_get(&sheet, 2u, 1u, &cell) == KHZ_SHEET_OK);
    CHECK(cell != NULL && cell->kind == (uint32_t)KHZ_CELL_FORMULA
          && cell->error == (uint32_t)KHZ_CELL_ERROR_NONE
          && cell->value.num == 10 && cell->value.den == 1);

    CHECK(khz_sheet_get(&sheet, 3u, 1u, &cell) == KHZ_SHEET_OK);
    CHECK(cell != NULL && cell->kind == (uint32_t)KHZ_CELL_FORMULA
          && cell->error == (uint32_t)KHZ_CELL_ERROR_NONE
          && cell->value.num == 20 && cell->value.den == 1);

    CHECK(khz_sheet_get(&sheet, 4u, 1u, &cell) == KHZ_SHEET_OK);
    CHECK(cell != NULL && cell->kind == (uint32_t)KHZ_CELL_FORMULA
          && cell->error == (uint32_t)KHZ_CELL_ERROR_NONE
          && cell->value.num == 7 && cell->value.den == 2);

    CHECK(khz_formula_set(&sheet, 0u, 2u, "ABS()", 5u, &perr) != KHZ_SHEET_OK);
    CHECK(khz_formula_set(&sheet, 0u, 2u, "IF(1,2)", 7u, &perr) != KHZ_SHEET_OK);
    CHECK(khz_formula_set(&sheet, 0u, 2u, "ABS(1,2)", 8u, &perr) != KHZ_SHEET_OK);

    CHECK(khz_sheet_verify_chain(&sheet, NULL) == KHZ_SHEET_OK);

    khz_sheet_destroy(&sheet);
    printf("checks=%d failures=%d\n", checks, failures);
    return failures != 0 ? 1 : 0;
}
