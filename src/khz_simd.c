#include "khz_simd.h"

#if defined(__AVX2__)
#  include <immintrin.h>
#  define KHZ_SIMD_IMPL_AVX2 1
#elif defined(__ARM_NEON) || defined(__ARM_NEON__) || defined(__aarch64__)
#  include <arm_neon.h>
#  define KHZ_SIMD_IMPL_NEON 1
#endif

/* 64-bit lane comparison is an aarch64 instruction. On 32-bit ARM the NEON
   unit has no vcgtq_s64, so the selection kernels below stay scalar there even
   though the sum kernel vectorises. Compiling a comparison that does not exist
   is not an option, and pretending otherwise would break the build on exactly
   the targets that need the honesty. */
#if defined(KHZ_SIMD_IMPL_NEON) && defined(__aarch64__)
#  define KHZ_SIMD_NEON_CMP64 1
#endif

#if defined(__GNUC__) || defined(__clang__)
#  define KHZ_HAS_BUILTIN_OVERFLOW 1
#endif

/* Returns 1 on overflow and leaves *out untouched. */
static int khz_add_checked(int64_t a, int64_t b, int64_t *out)
{
#ifdef KHZ_HAS_BUILTIN_OVERFLOW
    int64_t result;
    if (__builtin_add_overflow(a, b, &result)) {
        return 1;
    }
    *out = result;
    return 0;
#else
    if ((b > 0 && a > INT64_MAX - b) || (b < 0 && a < INT64_MIN - b)) {
        return 1;
    }
    *out = a + b;
    return 0;
#endif
}

KhzSimdKernel khz_simd_kernel(void)
{
#if defined(KHZ_SIMD_IMPL_AVX2)
    return KHZ_SIMD_AVX2;
#elif defined(KHZ_SIMD_IMPL_NEON)
    return KHZ_SIMD_NEON;
#else
    return KHZ_SIMD_SCALAR;
#endif
}

size_t khz_simd_lanes(void)
{
#if defined(KHZ_SIMD_IMPL_AVX2)
    return (size_t)4;
#elif defined(KHZ_SIMD_IMPL_NEON)
    return (size_t)2;
#else
    return (size_t)1;
#endif
}

const char *khz_simd_kernel_name(KhzSimdKernel kernel)
{
    switch (kernel) {
        case KHZ_SIMD_SCALAR:
            return "SCALAR";
        case KHZ_SIMD_AVX2:
            return "AVX2";
        case KHZ_SIMD_NEON:
            return "NEON";
        default:
            return "UNKNOWN";
    }
}

KhzSheetStatus khz_simd_sum_i64(const int64_t *values, size_t count, int64_t *out)
{
    int64_t total = (int64_t)0;
    size_t i = (size_t)0;

    if (out == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    if (count == (size_t)0) {
        *out = (int64_t)0;
        return KHZ_SHEET_OK;
    }

    if (values == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

#if defined(KHZ_SIMD_IMPL_AVX2)
    {
        __m256i acc = _mm256_setzero_si256();
        int64_t lanes[4];
        unsigned int lane;

        for (; i + (size_t)4 <= count; i += (size_t)4) {
            __m256i v = _mm256_loadu_si256((const __m256i *)(const void *)(values + i));
            __m256i r = _mm256_add_epi64(acc, v);

            /* Signed overflow happened in a lane exactly when the addends
               share a sign and the result differs from it: ((a^r) & (b^r)) has
               its top bit set. Four lanes are tested in one movemask, so the
               vector path is exact rather than merely fast - it produces the
               same total as the scalar fold or refuses outright. */
            __m256i ov = _mm256_and_si256(_mm256_xor_si256(acc, r),
                                          _mm256_xor_si256(v, r));

            if (_mm256_movemask_pd(_mm256_castsi256_pd(ov)) != 0) {
                return KHZ_SHEET_ERR_OVERFLOW;
            }

            acc = r;
        }

        _mm256_storeu_si256((__m256i *)(void *)lanes, acc);

        for (lane = 0u; lane < 4u; ++lane) {
            if (khz_add_checked(total, lanes[lane], &total)) {
                return KHZ_SHEET_ERR_OVERFLOW;
            }
        }
    }
#elif defined(KHZ_SIMD_IMPL_NEON)
    {
        int64x2_t acc = vdupq_n_s64((int64_t)0);
        int64_t lanes[2];
        unsigned int lane;

        for (; i + (size_t)2 <= count; i += (size_t)2) {
            int64x2_t v = vld1q_s64(values + i);
            int64x2_t r = vaddq_s64(acc, v);
            int64x2_t ov = vandq_s64(veorq_s64(acc, r), veorq_s64(v, r));

            if (vgetq_lane_s64(ov, 0) < (int64_t)0 || vgetq_lane_s64(ov, 1) < (int64_t)0) {
                return KHZ_SHEET_ERR_OVERFLOW;
            }

            acc = r;
        }

        vst1q_s64(lanes, acc);

        for (lane = 0u; lane < 2u; ++lane) {
            if (khz_add_checked(total, lanes[lane], &total)) {
                return KHZ_SHEET_ERR_OVERFLOW;
            }
        }
    }
#endif

    /* Tail, and the whole array on a scalar build. */
    for (; i < count; ++i) {
        if (khz_add_checked(total, values[i], &total)) {
            return KHZ_SHEET_ERR_OVERFLOW;
        }
    }

    *out = total;
    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_simd_sum_scaled(const int64_t *numerators, size_t count,
                                   int64_t den, KhzRational *out)
{
    int64_t total;
    KhzSheetStatus status;

    if (out == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    if (den <= (int64_t)0) {
        return KHZ_SHEET_ERR_RANGE;
    }

    status = khz_simd_sum_i64(numerators, count, &total);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    /* One normalisation at the end instead of one per element: the shared
       denominator makes the intermediate sums exact without reduction. */
    return khz_rational_make(total, den, out);
}

KhzSheetStatus khz_simd_sum_rational(const KhzRational *values, size_t count,
                                     KhzRational *out)
{
    KhzRational total;
    size_t i;

    if (out == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    if (count == (size_t)0) {
        *out = khz_rational_zero();
        return KHZ_SHEET_OK;
    }

    if (values == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    total = khz_rational_zero();

    /* Mixed denominators need a gcd per step, and there is no vector gcd. The
       fold is scalar and exact; the scaled path above is where the speed is. */
    for (i = (size_t)0; i < count; ++i) {
        KhzSheetStatus status = khz_rational_add(total, values[i], &total);

        if (status != KHZ_SHEET_OK) {
            return status;
        }
    }

    *out = total;
    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_simd_avg_rational(const KhzRational *values, size_t count,
                                     KhzRational *out)
{
    KhzRational total;
    KhzRational divisor;
    KhzSheetStatus status;

    if (out == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    /* The mean of an empty range is not zero, and returning zero would be a
       claim about data that is not there. */
    if (count == (size_t)0) {
        return KHZ_SHEET_ERR_RANGE;
    }

    if (count > (size_t)INT64_MAX) {
        return KHZ_SHEET_ERR_OVERFLOW;
    }

    status = khz_simd_sum_rational(values, count, &total);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    status = khz_rational_from_i64((int64_t)count, &divisor);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    /* Stays a rational: AVERAGE(1,2) is 3/2, not 1.5 and not 1. */
    return khz_rational_div(total, divisor, out);
}

/* ---------------------------------------------------------------------- *
 * Selection kernels.
 *
 * There is no _mm256_min_epi64 in AVX2 and no vminq_s64 in NEON: 64-bit
 * integer min/max arrived with AVX-512 and SVE. Both paths therefore compare
 * and blend, which is two instructions instead of one and still beats the
 * scalar loop by the lane count. No arithmetic is performed on the values, so
 * unlike SUM there is nothing here that can overflow.
 * ---------------------------------------------------------------------- */

KhzSheetStatus khz_simd_min_i64(const int64_t *values, size_t count, int64_t *out)
{
    int64_t best;
    size_t i = (size_t)0;

    if (out == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    /* No identity element exists for MIN, so an empty range is refused rather
       than answered with zero. */
    if (count == (size_t)0) {
        return KHZ_SHEET_ERR_RANGE;
    }

    if (values == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    best = values[0];

#if defined(KHZ_SIMD_IMPL_AVX2)
    if (count >= (size_t)4) {
        __m256i acc = _mm256_loadu_si256((const __m256i *)(const void *)values);
        int64_t lanes[4];
        unsigned int lane;

        for (i = (size_t)4; i + (size_t)4 <= count; i += (size_t)4) {
            __m256i v = _mm256_loadu_si256((const __m256i *)(const void *)(values + i));
            __m256i gt = _mm256_cmpgt_epi64(acc, v);

            /* Lane mask is all ones where acc > v, so the blend takes v there
               and keeps acc elsewhere. blendv_epi8 selects per byte, which is
               correct given a mask that is uniform across each 64-bit lane. */
            acc = _mm256_blendv_epi8(acc, v, gt);
        }

        _mm256_storeu_si256((__m256i *)(void *)lanes, acc);

        for (lane = 0u; lane < 4u; ++lane) {
            if (lanes[lane] < best) {
                best = lanes[lane];
            }
        }
    }
#elif defined(KHZ_SIMD_NEON_CMP64)
    if (count >= (size_t)2) {
        int64x2_t acc = vld1q_s64(values);
        int64_t lanes[2];
        unsigned int lane;

        for (i = (size_t)2; i + (size_t)2 <= count; i += (size_t)2) {
            int64x2_t v = vld1q_s64(values + i);
            uint64x2_t gt = vcgtq_s64(acc, v);

            acc = vbslq_s64(gt, v, acc);
        }

        vst1q_s64(lanes, acc);

        for (lane = 0u; lane < 2u; ++lane) {
            if (lanes[lane] < best) {
                best = lanes[lane];
            }
        }
    }
#endif

    /* Tail, and the whole array on a scalar build or on 32-bit ARM. */
    for (; i < count; ++i) {
        if (values[i] < best) {
            best = values[i];
        }
    }

    *out = best;
    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_simd_max_i64(const int64_t *values, size_t count, int64_t *out)
{
    int64_t best;
    size_t i = (size_t)0;

    if (out == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    if (count == (size_t)0) {
        return KHZ_SHEET_ERR_RANGE;
    }

    if (values == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    best = values[0];

#if defined(KHZ_SIMD_IMPL_AVX2)
    if (count >= (size_t)4) {
        __m256i acc = _mm256_loadu_si256((const __m256i *)(const void *)values);
        int64_t lanes[4];
        unsigned int lane;

        for (i = (size_t)4; i + (size_t)4 <= count; i += (size_t)4) {
            __m256i v = _mm256_loadu_si256((const __m256i *)(const void *)(values + i));
            __m256i gt = _mm256_cmpgt_epi64(v, acc);

            acc = _mm256_blendv_epi8(acc, v, gt);
        }

        _mm256_storeu_si256((__m256i *)(void *)lanes, acc);

        for (lane = 0u; lane < 4u; ++lane) {
            if (lanes[lane] > best) {
                best = lanes[lane];
            }
        }
    }
#elif defined(KHZ_SIMD_NEON_CMP64)
    if (count >= (size_t)2) {
        int64x2_t acc = vld1q_s64(values);
        int64_t lanes[2];
        unsigned int lane;

        for (i = (size_t)2; i + (size_t)2 <= count; i += (size_t)2) {
            int64x2_t v = vld1q_s64(values + i);
            uint64x2_t gt = vcgtq_s64(v, acc);

            acc = vbslq_s64(gt, v, acc);
        }

        vst1q_s64(lanes, acc);

        for (lane = 0u; lane < 2u; ++lane) {
            if (lanes[lane] > best) {
                best = lanes[lane];
            }
        }
    }
#endif

    for (; i < count; ++i) {
        if (values[i] > best) {
            best = values[i];
        }
    }

    *out = best;
    return KHZ_SHEET_OK;
}

/* Shared fold for the rational selection kernels. want is -1 for MIN and 1 for
   MAX: the candidate replaces the incumbent when the comparison matches. */
static KhzSheetStatus khz_simd_select_rational(const KhzRational *values, size_t count,
                                               int want, KhzRational *out)
{
    KhzRational best;
    size_t i;

    if (out == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    if (count == (size_t)0) {
        return KHZ_SHEET_ERR_RANGE;
    }

    if (values == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    best = values[0];

    for (i = (size_t)1; i < count; ++i) {
        int cmp = 0;
        KhzSheetStatus status = khz_rational_compare(values[i], best, &cmp);

        /* An overflowing cross product is propagated, not swallowed. Picking
           an element on the strength of a comparison that could not be
           computed would produce a confident wrong answer. */
        if (status != KHZ_SHEET_OK) {
            return status;
        }

        if (cmp == want) {
            best = values[i];
        }
    }

    *out = best;
    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_simd_min_rational(const KhzRational *values, size_t count,
                                     KhzRational *out)
{
    return khz_simd_select_rational(values, count, -1, out);
}

KhzSheetStatus khz_simd_max_rational(const KhzRational *values, size_t count,
                                     KhzRational *out)
{
    return khz_simd_select_rational(values, count, 1, out);
}
