#include <stdio.h>
#include <string.h>

#include "khz_formula.h"
#include "khz_sheet.h"

static int failures;

static void expect_status(const char *what, KhzSheetStatus got, KhzSheetStatus want)
{
    if (got != want) {
        fprintf(stderr, "FAIL %s: got %s want %s\n",
                what, khz_sheet_status_name(got), khz_sheet_status_name(want));
        ++failures;
    }
}

static void expect_true(const char *what, int condition)
{
    if (!condition) {
        fprintf(stderr, "FAIL %s\n", what);
        ++failures;
    }
}

int main(void)
{
    KhzSheet sheet;
    const KhzCell *cell = NULL;
    KhzRational invalid = { 1, 0 };

    expect_status("init", khz_sheet_init(&sheet, (size_t)1 << 20, (size_t)8), KHZ_SHEET_OK);

    expect_status("invalid rational", khz_sheet_set_rational(&sheet, 0u, 0u, invalid),
                  KHZ_SHEET_ERR_RANGE);
    expect_true("invalid rational created no cell", khz_sheet_cell_count(&sheet) == (size_t)0);
    expect_status("invalid rational coordinate remains missing",
                  khz_sheet_get(&sheet, 0u, 0u, &cell), KHZ_SHEET_ERR_MISSING);

    expect_status("invalid error", khz_sheet_set_error(&sheet, 0u, 1u, (KhzCellError)99),
                  KHZ_SHEET_ERR_RANGE);
    expect_true("invalid error created no cell", khz_sheet_cell_count(&sheet) == (size_t)0);
    expect_status("invalid error coordinate remains missing",
                  khz_sheet_get(&sheet, 0u, 1u, &cell), KHZ_SHEET_ERR_MISSING);

    {
        size_t before = khz_arena_used(khz_sheet_arena(&sheet));

        expect_status("text outside grid", khz_sheet_set_text(&sheet,
                      KHZ_GRID_MAX_COLUMNS, 0u, "x", (size_t)1), KHZ_SHEET_ERR_LIMIT);
        expect_true("invalid text consumes no arena",
                    khz_arena_used(khz_sheet_arena(&sheet)) == before);

        expect_status("formula outside grid", khz_sheet_set_formula(&sheet,
                      KHZ_GRID_MAX_COLUMNS, 0u, "1+1", (size_t)3), KHZ_SHEET_ERR_LIMIT);
        expect_true("invalid formula consumes no arena",
                    khz_arena_used(khz_sheet_arena(&sheet)) == before);
    }

    expect_status("seed A1", khz_sheet_set_i64(&sheet, 0u, 0u, 7), KHZ_SHEET_OK);
    expect_status("get A1", khz_sheet_get(&sheet, 0u, 0u, &cell), KHZ_SHEET_OK);

    if (cell != NULL) {
        KhzRational old_value = cell->value;
        uint64_t old_revision = cell->revision;
        unsigned char old_cell_proof[KHZ_SHA256_DIGEST_BYTES];
        unsigned char old_sheet_proof[KHZ_SHA256_DIGEST_BYTES];
        uint64_t old_recorded = sheet.log.recorded;
        size_t old_next = sheet.log.next;

        memcpy(old_cell_proof, cell->proof, sizeof old_cell_proof);
        memcpy(old_sheet_proof, sheet.proof, sizeof old_sheet_proof);

        sheet.commits = UINT64_MAX;
        expect_status("commit counter overflow", khz_sheet_set_i64(&sheet, 0u, 0u, 8),
                      KHZ_SHEET_ERR_OVERFLOW);

        expect_status("get A1 after overflow", khz_sheet_get(&sheet, 0u, 0u, &cell), KHZ_SHEET_OK);
        if (cell != NULL) {
            expect_true("value unchanged after overflow",
                        cell->value.num == old_value.num && cell->value.den == old_value.den);
            expect_true("revision unchanged after overflow", cell->revision == old_revision);
            expect_true("cell proof unchanged after overflow",
                        memcmp(cell->proof, old_cell_proof, sizeof old_cell_proof) == 0);
        }
        expect_true("sheet proof unchanged after overflow",
                    memcmp(sheet.proof, old_sheet_proof, sizeof old_sheet_proof) == 0);
        expect_true("log recorded unchanged after overflow", sheet.log.recorded == old_recorded);
        expect_true("log cursor unchanged after overflow", sheet.log.next == old_next);
    }

    {
        KhzSheet tight;
        size_t before;

        expect_status("tight init", khz_sheet_init(&tight, (size_t)1 << 20, (size_t)1),
                      KHZ_SHEET_OK);
        expect_status("tight seed", khz_sheet_set_i64(&tight, 0u, 0u, 1), KHZ_SHEET_OK);
        before = khz_arena_used(khz_sheet_arena(&tight));

        expect_status("text at full capacity", khz_sheet_set_text(&tight, 1u, 0u,
                      "rejected", (size_t)8), KHZ_SHEET_ERR_LIMIT);
        expect_true("full-capacity text consumes no arena",
                    khz_arena_used(khz_sheet_arena(&tight)) == before);

        expect_status("formula at full capacity", khz_sheet_set_formula(&tight, 1u, 0u,
                      "A1+1", (size_t)4), KHZ_SHEET_ERR_LIMIT);
        expect_true("full-capacity formula consumes no arena",
                    khz_arena_used(khz_sheet_arena(&tight)) == before);

        khz_sheet_destroy(&tight);
    }

    /* Recalculation must not publish a new cached value when the commit that
       authenticates it is refused. Saturating the formula cell revision forces
       commit preflight to fail after evaluation without requiring any corrupt
       allocator or hash state. */
    {
        KhzSheet recalc;
        KhzCell *formula_cell = NULL;
        KhzRational old_value = { 0, 1 };
        uint32_t old_error = (uint32_t)KHZ_CELL_ERROR_NONE;
        uint32_t old_flags = 0u;
        uint64_t evaluated = (uint64_t)0;

        expect_status("recalc init", khz_sheet_init(&recalc, (size_t)1 << 20, (size_t)8),
                      KHZ_SHEET_OK);
        expect_status("recalc A1 = 1", khz_sheet_set_i64(&recalc, 0u, 0u, 1), KHZ_SHEET_OK);
        expect_status("recalc B1 = A1",
                      khz_formula_set(&recalc, 1u, 0u, "A1", (size_t)2, NULL), KHZ_SHEET_OK);
        expect_status("initial formula recalc", khz_formula_recalc(&recalc, &evaluated),
                      KHZ_SHEET_OK);
        expect_status("recalc A1 = 2", khz_sheet_set_i64(&recalc, 0u, 0u, 2), KHZ_SHEET_OK);
        expect_status("get mutable B1", khz_sheet_get_mutable(&recalc, 1u, 0u, &formula_cell),
                      KHZ_SHEET_OK);

        if (formula_cell != NULL) {
            old_value = formula_cell->value;
            old_error = formula_cell->error;
            old_flags = formula_cell->flags;
            formula_cell->revision = UINT64_MAX;

            expect_status("recalc commit refusal", khz_formula_recalc(&recalc, &evaluated),
                          KHZ_SHEET_ERR_OVERFLOW);
            expect_true("recalc value restored after refusal",
                        formula_cell->value.num == old_value.num
                        && formula_cell->value.den == old_value.den);
            expect_true("recalc error restored after refusal", formula_cell->error == old_error);
            expect_true("recalc dirty state restored after refusal", formula_cell->flags == old_flags);
            expect_true("recalc remains dirty after refusal",
                        (formula_cell->flags & (uint32_t)KHZ_CELL_FLAG_DIRTY) != 0u);
        }

        khz_sheet_destroy(&recalc);
    }

    {
        size_t failed = (size_t)123;
        expect_status("verify null sheet", khz_sheet_verify_chain(NULL, &failed), KHZ_SHEET_ERR_NULL);
        expect_true("verify failure index is defined", failed == (size_t)0);
    }

    khz_sheet_destroy(&sheet);
    printf("%s failures=%d\n", failures == 0 ? "ALL PASS" : "FAIL", failures);
    return failures == 0 ? 0 : 1;
}