/* The sheet layer's self-test.

   Split out of src/khz_sheet.c in Phase 98. The move was mechanical: the body
   below is unchanged, and it is here because khz_sheet.c had grown past the
   size at which re-transmitting the whole file to change a few lines is safe.
   khz_formula is split across three translation units for the same reason.

   Everything this test touches is part of the public sheet surface, which is
   why the split needed no other change: a self-test that required internal
   access would be testing the implementation rather than the contract. */

#include "khz_sheet.h"

#include "khz_formula.h"
#include "khz_hash.h"
#include "khz_simd.h"

int khz_sheet_selftest(void)
{
    int failures = 0;

    failures += khz_sha256_selftest();
    failures += khz_fnv1a_selftest();

    /* Exact rational: 1/2 + 1/3 must be 5/6, not 0.8333333333333334. */
    {
        KhzRational a;
        KhzRational b;
        KhzRational sum;

        if (khz_rational_make((int64_t)1, (int64_t)2, &a) != KHZ_SHEET_OK
            || khz_rational_make((int64_t)1, (int64_t)3, &b) != KHZ_SHEET_OK
            || khz_rational_add(a, b, &sum) != KHZ_SHEET_OK
            || sum.num != (int64_t)5 || sum.den != (int64_t)6) {
            ++failures;
        }
    }

    /* Normalisation and sign placement: -2/-4 is 1/2. */
    {
        KhzRational r;

        if (khz_rational_make((int64_t)-2, (int64_t)-4, &r) != KHZ_SHEET_OK
            || r.num != (int64_t)1 || r.den != (int64_t)2) {
            ++failures;
        }
    }

    /* Division by zero is a status, never a trap and never an infinity. */
    {
        KhzRational r;

        if (khz_rational_make((int64_t)1, (int64_t)0, &r) != KHZ_SHEET_ERR_DIVZERO) {
            ++failures;
        }
    }

    /* Overflow is refused, not wrapped. */
    {
        KhzRational big;
        KhzRational one;
        KhzRational sum;

        if (khz_rational_from_i64(INT64_MAX, &big) != KHZ_SHEET_OK
            || khz_rational_from_i64((int64_t)1, &one) != KHZ_SHEET_OK
            || khz_rational_add(big, one, &sum) != KHZ_SHEET_ERR_OVERFLOW) {
            ++failures;
        }
    }

    /* Comparison itself must not overflow. These two valid canonical
       rationals are both just below one; direct cross-products exceed int64,
       but their order is exact and representable: (M-1)/M > (M-2)/(M-1). */
    {
        KhzRational a;
        KhzRational b;
        int cmp = 0;

        if (khz_rational_make(INT64_MAX - (int64_t)1, INT64_MAX, &a) != KHZ_SHEET_OK
            || khz_rational_make(INT64_MAX - (int64_t)2,
                                 INT64_MAX - (int64_t)1, &b) != KHZ_SHEET_OK
            || khz_rational_compare(a, b, &cmp) != KHZ_SHEET_OK
            || cmp <= 0) {
            ++failures;
        }
    }

    /* The SIMD kernel must agree with the scalar fold and must refuse a sum
       that does not fit. */
    {
        int64_t values[9];
        int64_t total = (int64_t)0;
        size_t i;

        for (i = (size_t)0; i < (size_t)9; ++i) {
            values[i] = (int64_t)(i + (size_t)1);
        }

        if (khz_simd_sum_i64(values, (size_t)9, &total) != KHZ_SHEET_OK
            || total != (int64_t)45) {
            ++failures;
        }

        {
            int64_t edge[4];
            int64_t ignored = (int64_t)0;

            edge[0] = INT64_MAX;
            edge[1] = INT64_MAX;
            edge[2] = (int64_t)0;
            edge[3] = (int64_t)0;

            if (khz_simd_sum_i64(edge, (size_t)4, &ignored) != KHZ_SHEET_ERR_OVERFLOW) {
                ++failures;
            }
        }
    }

    /* MIN and MAX select, and refuse an empty input rather than inventing a
       zero for it. */
    {
        int64_t values[5];
        int64_t low = (int64_t)0;
        int64_t high = (int64_t)0;

        values[0] = (int64_t)7;
        values[1] = (int64_t)-3;
        values[2] = (int64_t)0;
        values[3] = INT64_MIN;
        values[4] = INT64_MAX;

        if (khz_simd_min_i64(values, (size_t)5, &low) != KHZ_SHEET_OK
            || low != INT64_MIN) {
            ++failures;
        }
        if (khz_simd_max_i64(values, (size_t)5, &high) != KHZ_SHEET_OK
            || high != INT64_MAX) {
            ++failures;
        }
        if (khz_simd_min_i64(values, (size_t)0, &low) != KHZ_SHEET_ERR_RANGE) {
            ++failures;
        }
    }

    /* End to end: a small sheet, an exact mean, a proof chain that verifies
       across a rewrite, and an arena that took every byte. */
    {
        KhzSheet sheet;

        if (khz_sheet_init(&sheet, (size_t)1 << 20, (size_t)1024) != KHZ_SHEET_OK) {
            ++failures;
        } else {
            KhzRational mean;
            size_t counted = (size_t)0;
            int local = 0;

            if (khz_sheet_set_i64(&sheet, 0u, 0u, (int64_t)1) != KHZ_SHEET_OK
                || khz_sheet_set_i64(&sheet, 0u, 1u, (int64_t)2) != KHZ_SHEET_OK) {
                ++local;
            }

            if (khz_sheet_avg(&sheet, 0u, 0u, 0u, 1u, &mean, &counted) != KHZ_SHEET_OK
                || mean.num != (int64_t)3 || mean.den != (int64_t)2
                || counted != (size_t)2) {
                ++local;
            }

            if (khz_sheet_set_text(&sheet, 0u, 2u, "label", (size_t)5) != KHZ_SHEET_OK) {
                ++local;
            }

            if (khz_sheet_avg(&sheet, 0u, 0u, 0u, 2u, &mean, &counted) != KHZ_SHEET_OK
                || mean.num != (int64_t)3 || mean.den != (int64_t)2
                || counted != (size_t)2) {
                ++local;
            }

            /* KhzFormulaNode is public, so evaluator-side shape validation is
               part of the contract. A malformed aggregate must be refused as
               ERR_FORMAT rather than dereferencing a missing child array. */
            {
                KhzFormula malformed = {0};
                KhzFormulaNode root = {0};
                KhzFormulaResult result;

                malformed.arena = &sheet.arena;
                malformed.root = &root;
                root.op = (uint32_t)KHZ_FORMULA_SUM;
                root.child_count = 1u;
                root.children = NULL;

                if (khz_formula_eval(&sheet, &malformed, &result) != KHZ_SHEET_ERR_FORMAT) {
                    ++local;
                }
            }

            if (khz_sheet_verify_chain(&sheet, NULL) != KHZ_SHEET_OK) {
                ++local;
            }

            {
                KhzChainAudit audit;

                if (khz_sheet_set_i64(&sheet, 0u, 0u, (int64_t)5) != KHZ_SHEET_OK) {
                    ++local;
                }
                if (khz_sheet_audit_chain(&sheet, &audit) != KHZ_SHEET_OK) {
                    ++local;
                }
                if (audit.superseded != (uint64_t)1) {
                    ++local;
                }
                if (audit.entries_examined != khz_sheet_log_recorded(&sheet)) {
                    ++local;
                }
                if (khz_sheet_log_dropped(&sheet) != (uint64_t)0) {
                    ++local;
                }
            }

            {
                KhzCell *cell = NULL;

                if (khz_sheet_get_mutable(&sheet, 0u, 1u, &cell) != KHZ_SHEET_OK
                    || cell == NULL) {
                    ++local;
                } else {
                    KhzRational saved = cell->value;

                    cell->value.num += (int64_t)1;
                    if (khz_sheet_verify_chain(&sheet, NULL) == KHZ_SHEET_OK) {
                        ++local;
                    }
                    cell->value = saved;
                    if (khz_sheet_verify_chain(&sheet, NULL) != KHZ_SHEET_OK) {
                        ++local;
                    }
                }
            }

            {
                uint64_t dirtied = (uint64_t)0;

                if (khz_sheet_set_formula(&sheet, 1u, 5u, "=A1+A2", (size_t)6)
                    != KHZ_SHEET_OK) {
                    ++local;
                }
                if (khz_sheet_declare_dependency(&sheet, 0u, 0u, 1u, 5u)
                    != KHZ_SHEET_OK) {
                    ++local;
                }
                if (khz_sheet_mark_dirty(&sheet, 0u, 0u, &dirtied) != KHZ_SHEET_OK
                    || dirtied != (uint64_t)1) {
                    ++local;
                }
                if (khz_sheet_dirty_count(&sheet) != (size_t)1) {
                    ++local;
                }
            }

            if (khz_sheet_declare_dependency(&sheet, 2u, 0u, 2u, 1u) != KHZ_SHEET_OK
                || khz_sheet_declare_dependency(&sheet, 2u, 1u, 2u, 0u) != KHZ_SHEET_OK) {
                ++local;
            } else {
                size_t order[64];
                size_t produced = (size_t)0;

                if (khz_sheet_evaluation_order(&sheet, order, (size_t)64, &produced)
                    != KHZ_SHEET_ERR_CYCLE) {
                    ++local;
                }
            }

            if (khz_arena_rejections(&sheet.arena) != (uint64_t)0) {
                ++local;
            }
            if (khz_arena_peak(&sheet.arena) == (size_t)0) {
                ++local;
            }

            failures += local;
            khz_sheet_destroy(&sheet);
        }
    }

    return failures;
}
