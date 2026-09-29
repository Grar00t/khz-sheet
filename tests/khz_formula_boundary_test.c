#include "khz_formula.h"
#include <stdio.h>
#include <string.h>
#define CHECK(c) do { ++checks; if (!(c)) { \
    fprintf(stderr, "FAIL line %d: %s\n", __LINE__, #c); ++failures; } } while (0)
int main(void)
{
    KhzArena arena;
    KhzFormula formula;
    KhzFormulaNode *node = NULL;
    KhzFormulaParseError error;
    size_t mark;
    char source[KHZ_FORMULA_MAX_DEPTH * 2 + 2];
    int checks = 0, failures = 0;
    if (khz_arena_init(&arena, 1048576) != KHZ_ARENA_OK) return 2;
    CHECK(khz_formula_begin(&formula, &arena, 0, 0) == KHZ_SHEET_OK);
    mark = khz_arena_used(&arena);
    CHECK(khz_formula_const(&formula, (KhzRational){1, 1}, NULL) == KHZ_SHEET_ERR_NULL);
    CHECK(khz_formula_ref(&formula, 0, 0, NULL) == KHZ_SHEET_ERR_NULL);
    CHECK(khz_formula_range(&formula, 0, 0, 1, 1, NULL) == KHZ_SHEET_ERR_NULL);
    CHECK(khz_arena_used(&arena) == mark && formula.node_count == 0);
    CHECK(khz_formula_const(NULL, (KhzRational){1, 1}, &node) == KHZ_SHEET_ERR_NULL);
    CHECK(khz_formula_ref(NULL, 0, 0, &node) == KHZ_SHEET_ERR_NULL);
    CHECK(khz_formula_range(NULL, 0, 0, 1, 1, &node) == KHZ_SHEET_ERR_NULL);
    CHECK(node == NULL);
    for (int sign = 0; sign < 2; ++sign) {
        memset(source, sign ? '-' : '+', KHZ_FORMULA_MAX_DEPTH - 1);
        source[KHZ_FORMULA_MAX_DEPTH - 1] = '1';
        CHECK(khz_formula_parse(&formula, &arena, 0, 0, source,
                                KHZ_FORMULA_MAX_DEPTH, &error) == KHZ_SHEET_OK);
        khz_formula_abandon(&formula);
        CHECK(khz_arena_used(&arena) == mark);
        memset(source, sign ? '-' : '+', KHZ_FORMULA_MAX_DEPTH);
        source[KHZ_FORMULA_MAX_DEPTH] = '1';
        CHECK(khz_formula_parse(&formula, &arena, 0, 0, source,
                                KHZ_FORMULA_MAX_DEPTH + 1, &error) == KHZ_SHEET_ERR_LIMIT);
        CHECK(khz_arena_used(&arena) == mark);
        CHECK(error.offset > 0 && error.offset <= KHZ_FORMULA_MAX_DEPTH + 1);
        CHECK(formula.root == NULL);
    }
    CHECK(khz_formula_parse(&formula, &arena, 0, 0, "1+", 2, &error) == KHZ_SHEET_ERR_FORMAT);
    CHECK(khz_arena_used(&arena) == mark);
    khz_arena_destroy(&arena);
    printf("checks=%d failures=%d\n", checks, failures);
    return failures ? 1 : 0;
}
