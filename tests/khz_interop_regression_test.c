#include "khz_abi.h"
#include "khz_formula.h"
#include "khz_sheet.h"
#include <inttypes.h>
#include <stdio.h>
#include <string.h>

#define CHECK(x) do { if (!(x)) { fprintf(stderr, "FAIL line %d: %s\n", __LINE__, #x); ++failures; } } while (0)

int main(void)
{
    int failures = 0;
    KhzSheet sheet;
    KhzRational value = {7, 9};
    const KhzCell *cell = NULL;
    uint64_t evaluated = 0;
    char before[KHZ_SHA256_HEX_BYTES], after[KHZ_SHA256_HEX_BYTES];
    size_t failed = 0;
    CHECK(khz_abi_version() == KHZ_ABI_VERSION);
    CHECK(khz_abi_sheet_bytes() == sizeof(KhzSheet));
    CHECK(khz_rational_make(INT64_MIN, 1, &value) == KHZ_SHEET_ERR_OVERFLOW);
    CHECK(value.num == 7 && value.den == 9);
    CHECK(khz_rational_neg((KhzRational){INT64_MIN, 1}, &value) == KHZ_SHEET_ERR_RANGE);
    CHECK(khz_rational_make(1, 0, &value) == KHZ_SHEET_ERR_DIVZERO);
    CHECK(khz_rational_make(-2, -4, &value) == KHZ_SHEET_OK && value.num == 1 && value.den == 2);
    if (khz_sheet_init(&sheet, 1024 * 1024, 32) != KHZ_SHEET_OK) return 2;
    CHECK(khz_sheet_set_i64(&sheet, 0, 0, 10) == KHZ_SHEET_OK);
    CHECK(khz_formula_set(&sheet, 1, 0, "=A1*2", 5, NULL) == KHZ_SHEET_OK);
    CHECK(khz_formula_set(&sheet, 2, 0, "=B1+1", 5, NULL) == KHZ_SHEET_OK);
    CHECK(khz_formula_recalc(&sheet, &evaluated) == KHZ_SHEET_OK && evaluated == 2);
    CHECK(khz_sheet_get(&sheet, 2, 0, &cell) == KHZ_SHEET_OK);
    CHECK(cell != NULL && cell->value.num == 21 && cell->value.den == 1);
    CHECK(khz_sheet_proof_hex(&sheet, before) == KHZ_SHEET_OK);
    CHECK(khz_sheet_set_i64(&sheet, 0, 0, 7) == KHZ_SHEET_OK);
    CHECK(khz_formula_recalc(&sheet, &evaluated) == KHZ_SHEET_OK && evaluated == 2);
    CHECK(cell->value.num == 15 && cell->value.den == 1);
    CHECK(khz_sheet_proof_hex(&sheet, after) == KHZ_SHEET_OK && strcmp(before, after) != 0);
    CHECK(khz_sheet_verify_chain(&sheet, &failed) == KHZ_SHEET_OK);
    {
        const char text[] = "A\0B\xd8\xb9";
        CHECK(khz_sheet_set_text(&sheet, 3, 0, text, sizeof text) == KHZ_SHEET_OK);
        CHECK(khz_sheet_get(&sheet, 3, 0, &cell) == KHZ_SHEET_OK);
        CHECK(cell->text_len == sizeof text && memcmp(cell->text, text, sizeof text) == 0);
        size_t mark = khz_sheet_mark(&sheet);
        uint64_t commits = khz_sheet_commits(&sheet);
        CHECK(khz_sheet_set_text(&sheet, KHZ_GRID_MAX_COLUMNS, 0, text, sizeof text) == KHZ_SHEET_ERR_LIMIT);
        CHECK(khz_sheet_mark(&sheet) == mark && khz_sheet_commits(&sheet) == commits);
        CHECK(khz_formula_set(&sheet, 0, 0, "=1+", 3, NULL) != KHZ_SHEET_OK);
        CHECK(khz_sheet_mark(&sheet) == mark && khz_sheet_commits(&sheet) == commits);
    }
    khz_sheet_destroy(&sheet);
    khz_sheet_destroy(&sheet);

    /* A later allocation failure must report earlier committed evaluations. */
    if (khz_sheet_init(&sheet, 1024 * 1024, 32) != KHZ_SHEET_OK) return 2;
    CHECK(khz_formula_set(&sheet, 0, 0, "=2", 2, NULL) == KHZ_SHEET_OK);
    {
        char source[256] = "=1";
        for (size_t i = 0; i < 40; ++i) memcpy(source + 2 + i * 2, "+1", 2);
        source[82] = '\0';
        CHECK(khz_formula_set(&sheet, 1, 0, source, strlen(source), NULL) == KHZ_SHEET_OK);
        size_t remaining = khz_arena_remaining(&sheet.arena);
        CHECK(remaining > 2048);
        CHECK(khz_arena_alloc(&sheet.arena, remaining - 2048) != NULL);
        uint64_t commits = sheet.commits;
        KhzSheetStatus status = khz_formula_recalc(&sheet, &evaluated);
        printf("partial_status=%s evaluated=%" PRIu64 " committed=%" PRIu64 "\n",
               khz_sheet_status_name(status), evaluated, sheet.commits - commits);
        CHECK(status == KHZ_SHEET_ERR_MEMORY);
        CHECK(sheet.commits - commits == 1);
        CHECK(evaluated == 1);
        CHECK(khz_sheet_verify_chain(&sheet, &failed) == KHZ_SHEET_OK);
    }
    khz_sheet_destroy(&sheet);
    printf("interop_failures=%d\n", failures);
    return failures == 0 ? 0 : 1;
}
