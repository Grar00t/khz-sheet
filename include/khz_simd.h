#ifndef KHZ_SIMD_H
#define KHZ_SIMD_H

#include <stddef.h>
#include <stdint.h>

#include "khz_rational.h"
#include "khz_status.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef enum KhzSimdKernel {
    KHZ_SIMD_SCALAR = 0,
    KHZ_SIMD_AVX2   = 1,
    KHZ_SIMD_NEON   = 2
} KhzSimdKernel;

/* Which kernel this translation unit was compiled with. Decided at compile
   time by the flags actually used, not probed at runtime, so the answer cannot
   disagree with the code that runs. */
KhzSimdKernel khz_simd_kernel(void);
const char *khz_simd_kernel_name(KhzSimdKernel kernel);

/* Lanes per vector for the compiled kernel: 4 under AVX2, 2 under NEON, 1
   otherwise. Reported so a benchmark can state its own conditions. */
size_t khz_simd_lanes(void);

/* Exact sum of int64 values. Overflow is detected per lane inside the vector
   loop using the sign rule ((a^r) & (b^r)) < 0 and returns
   KHZ_SHEET_ERR_OVERFLOW with *out untouched. The vector path is therefore not
   a fast approximation of the scalar path - it computes the same value or
   refuses, and the two paths agree bit for bit on every input.
   count == 0 yields 0. */
KhzSheetStatus khz_simd_sum_i64(const int64_t *values, size_t count, int64_t *out);

/* Exact sum of rationals with a shared denominator. The numerators are summed
   with khz_simd_sum_i64 and the result normalised once. This is the path the
   sheet uses when a range turns out to be integer or uniformly scaled, which
   is the common case. den must be positive. */
KhzSheetStatus khz_simd_sum_scaled(const int64_t *numerators, size_t count,
                                   int64_t den, KhzRational *out);

/* Exact sum of arbitrary rationals: a scalar fold through khz_rational_add,
   with a normalisation at every step. No SIMD, because the denominators
   differ and there is no vector gcd. Correctness first; the scaled path above
   is where the speed is. */
KhzSheetStatus khz_simd_sum_rational(const KhzRational *values, size_t count,
                                     KhzRational *out);

/* Exact mean: the sum divided by count, still a rational. AVERAGE of 1 and 2
   is 3/2, not 1.5, and not 1. count == 0 is KHZ_SHEET_ERR_RANGE, because the
   mean of nothing is not zero. */
KhzSheetStatus khz_simd_avg_rational(const KhzRational *values, size_t count,
                                     KhzRational *out);

/* Smallest and largest int64 in the array.

   count == 0 is KHZ_SHEET_ERR_RANGE, not zero. SUM has an identity element and
   MIN does not: the smallest of nothing is not a number, and returning 0 would
   be a claim about data that is not there. This asymmetry with khz_simd_sum_i64
   is deliberate.

   These cannot overflow. The result is always one of the inputs, so there is
   no arithmetic to trap and no KHZ_SHEET_ERR_OVERFLOW path - claiming to check
   for one would be theatre. Error handling is otherwise identical to SUM: null
   arguments are refused and *out is untouched on any failure.

   Vectorised with compare-and-blend rather than a min instruction, because
   64-bit integer min is AVX-512 and SVE, not AVX2 or baseline NEON. The vector
   and scalar paths select the same element on every input. */
KhzSheetStatus khz_simd_min_i64(const int64_t *values, size_t count, int64_t *out);
KhzSheetStatus khz_simd_max_i64(const int64_t *values, size_t count, int64_t *out);

/* Smallest and largest rational, compared exactly.

   Scalar by necessity: arbitrary rational ordering does not map cleanly onto
   the fixed-width SIMD lanes used here. khz_rational_compare uses an
   overflow-free quotient/remainder comparison, so selection itself has no
   cross-product overflow path and always returns one of the valid inputs.

   count == 0 is KHZ_SHEET_ERR_RANGE. */
KhzSheetStatus khz_simd_min_rational(const KhzRational *values, size_t count,
                                     KhzRational *out);
KhzSheetStatus khz_simd_max_rational(const KhzRational *values, size_t count,
                                     KhzRational *out);

#ifdef __cplusplus
}
#endif

#endif /* KHZ_SIMD_H */
