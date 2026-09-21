#include "khz_rational.h"

#include <inttypes.h>
#include <stdio.h>

#include "khz_hash.h"

#if defined(__GNUC__) || defined(__clang__)
#  define KHZ_HAS_BUILTIN_OVERFLOW 1
#endif

/* Returns 1 on overflow and leaves *out untouched. Nothing in this file wraps
   silently; every arithmetic step is checked before it is kept. */
static int khz_i64_add(int64_t a, int64_t b, int64_t *out)
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

static int khz_i64_mul(int64_t a, int64_t b, int64_t *out)
{
#ifdef KHZ_HAS_BUILTIN_OVERFLOW
    int64_t result;
    if (__builtin_mul_overflow(a, b, &result)) {
        return 1;
    }
    *out = result;
    return 0;
#elif defined(__SIZEOF_INT128__)
    __int128 wide = (__int128)a * (__int128)b;
    if (wide > (__int128)INT64_MAX || wide < (__int128)INT64_MIN) {
        return 1;
    }
    *out = (int64_t)wide;
    return 0;
#else
    if (a == 0 || b == 0) {
        *out = 0;
        return 0;
    }
    if (a == INT64_MIN || b == INT64_MIN) {
        return 1;
    }
    {
        int64_t sa = a < 0 ? -a : a;
        int64_t sb = b < 0 ? -b : b;
        if (sa > INT64_MAX / sb) {
            return 1;
        }
    }
    *out = a * b;
    return 0;
#endif
}

static uint64_t khz_gcd_u64(uint64_t a, uint64_t b)
{
    while (b != (uint64_t)0) {
        uint64_t t = a % b;
        a = b;
        b = t;
    }
    return a;
}

static uint64_t khz_abs_u64(int64_t value)
{
    /* value == INT64_MIN is rejected upstream, so the negation below cannot
       overflow by the time it is reached. */
    return value < 0 ? (uint64_t)(-value) : (uint64_t)value;
}

/* Exact ordering of two non-negative fractions without cross multiplication.

   Each step compares the integer quotients from Euclid's algorithm. If they
   match, the remaining fractional parts are inverted; inversion reverses the
   order, so `sense` flips. All operands stay within the original uint64 range
   and the loop terminates as the remainders shrink. This is effectively a
   continued-fraction comparison and cannot overflow. */
static int khz_fraction_compare_u64(uint64_t an, uint64_t ad,
                                    uint64_t bn, uint64_t bd)
{
    int sense = 1;

    for (;;) {
        uint64_t aq = an / ad;
        uint64_t ar = an % ad;
        uint64_t bq = bn / bd;
        uint64_t br = bn % bd;

        if (aq < bq) {
            return -sense;
        }
        if (aq > bq) {
            return sense;
        }

        if (ar == (uint64_t)0 || br == (uint64_t)0) {
            if (ar == (uint64_t)0 && br == (uint64_t)0) {
                return 0;
            }
            return ar == (uint64_t)0 ? -sense : sense;
        }

        an = ad;
        ad = ar;
        bn = bd;
        bd = br;
        sense = -sense;
    }
}

static void khz_store_le64(unsigned char *p, uint64_t value)
{
    unsigned int i;
    for (i = 0u; i < 8u; ++i) {
        p[i] = (unsigned char)((value >> (8u * i)) & 0xffu);
    }
}

KhzRational khz_rational_zero(void)
{
    KhzRational r;
    r.num = (int64_t)0;
    r.den = (int64_t)1;
    return r;
}

KhzRational khz_rational_one(void)
{
    KhzRational r;
    r.num = (int64_t)1;
    r.den = (int64_t)1;
    return r;
}

int khz_rational_is_valid(KhzRational a)
{
    uint64_t g;

    if (a.den <= (int64_t)0) {
        return 0;
    }
    if (a.num == INT64_MIN || a.den == INT64_MIN) {
        return 0;
    }
    if (a.num == (int64_t)0) {
        return a.den == (int64_t)1 ? 1 : 0;
    }

    g = khz_gcd_u64(khz_abs_u64(a.num), (uint64_t)a.den);
    return g == (uint64_t)1 ? 1 : 0;
}

KhzSheetStatus khz_rational_make(int64_t num, int64_t den, KhzRational *out)
{
    uint64_t g;

    if (out == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    if (den == (int64_t)0) {
        return KHZ_SHEET_ERR_DIVZERO;
    }

    if (num == INT64_MIN || den == INT64_MIN) {
        return KHZ_SHEET_ERR_OVERFLOW;
    }

    if (den < (int64_t)0) {
        num = -num;
        den = -den;
    }

    if (num == (int64_t)0) {
        out->num = (int64_t)0;
        out->den = (int64_t)1;
        return KHZ_SHEET_OK;
    }

    g = khz_gcd_u64(khz_abs_u64(num), (uint64_t)den);
    if (g > (uint64_t)1) {
        num = num / (int64_t)g;
        den = den / (int64_t)g;
    }

    out->num = num;
    out->den = den;
    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_rational_from_i64(int64_t value, KhzRational *out)
{
    return khz_rational_make(value, (int64_t)1, out);
}

KhzSheetStatus khz_rational_add(KhzRational a, KhzRational b, KhzRational *out)
{
    uint64_t g;
    int64_t ad;
    int64_t bd;
    int64_t den;
    int64_t left;
    int64_t right;
    int64_t num;

    if (out == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }
    if (!khz_rational_is_valid(a) || !khz_rational_is_valid(b)) {
        return KHZ_SHEET_ERR_RANGE;
    }

    /* Reduce the denominators against each other first. The product a.den *
       b.den overflows far sooner than the lcm, and using it would refuse sums
       that are perfectly representable. */
    g = khz_gcd_u64((uint64_t)a.den, (uint64_t)b.den);
    ad = a.den / (int64_t)g;
    bd = b.den / (int64_t)g;

    if (khz_i64_mul(a.den, bd, &den)) {
        return KHZ_SHEET_ERR_OVERFLOW;
    }
    if (khz_i64_mul(a.num, bd, &left)) {
        return KHZ_SHEET_ERR_OVERFLOW;
    }
    if (khz_i64_mul(b.num, ad, &right)) {
        return KHZ_SHEET_ERR_OVERFLOW;
    }
    if (khz_i64_add(left, right, &num)) {
        return KHZ_SHEET_ERR_OVERFLOW;
    }

    return khz_rational_make(num, den, out);
}

KhzSheetStatus khz_rational_neg(KhzRational a, KhzRational *out)
{
    if (out == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }
    if (!khz_rational_is_valid(a)) {
        return KHZ_SHEET_ERR_RANGE;
    }

    out->num = -a.num;
    out->den = a.den;
    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_rational_abs(KhzRational a, KhzRational *out)
{
    if (out == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }
    if (!khz_rational_is_valid(a)) {
        return KHZ_SHEET_ERR_RANGE;
    }

    out->num = a.num < (int64_t)0 ? -a.num : a.num;
    out->den = a.den;
    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_rational_sub(KhzRational a, KhzRational b, KhzRational *out)
{
    KhzRational negated;
    KhzSheetStatus status = khz_rational_neg(b, &negated);

    if (status != KHZ_SHEET_OK) {
        return status;
    }

    return khz_rational_add(a, negated, out);
}

KhzSheetStatus khz_rational_mul(KhzRational a, KhzRational b, KhzRational *out)
{
    uint64_t g1;
    uint64_t g2;
    int64_t an;
    int64_t bn;
    int64_t ad;
    int64_t bd;
    int64_t num;
    int64_t den;

    if (out == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }
    if (!khz_rational_is_valid(a) || !khz_rational_is_valid(b)) {
        return KHZ_SHEET_ERR_RANGE;
    }

    /* Cross-reduce before multiplying, for the same reason as in add: the
       reduced product fits in cases the raw product does not. */
    g1 = khz_gcd_u64(khz_abs_u64(a.num), (uint64_t)b.den);
    g2 = khz_gcd_u64(khz_abs_u64(b.num), (uint64_t)a.den);

    an = g1 > (uint64_t)1 ? a.num / (int64_t)g1 : a.num;
    bd = g1 > (uint64_t)1 ? b.den / (int64_t)g1 : b.den;
    bn = g2 > (uint64_t)1 ? b.num / (int64_t)g2 : b.num;
    ad = g2 > (uint64_t)1 ? a.den / (int64_t)g2 : a.den;

    if (khz_i64_mul(an, bn, &num)) {
        return KHZ_SHEET_ERR_OVERFLOW;
    }
    if (khz_i64_mul(ad, bd, &den)) {
        return KHZ_SHEET_ERR_OVERFLOW;
    }

    return khz_rational_make(num, den, out);
}

KhzSheetStatus khz_rational_div(KhzRational a, KhzRational b, KhzRational *out)
{
    KhzRational reciprocal;
    KhzSheetStatus status;

    if (out == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }
    if (!khz_rational_is_valid(a) || !khz_rational_is_valid(b)) {
        return KHZ_SHEET_ERR_RANGE;
    }
    if (b.num == (int64_t)0) {
        return KHZ_SHEET_ERR_DIVZERO;
    }

    status = khz_rational_make(b.den, b.num, &reciprocal);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    return khz_rational_mul(a, reciprocal, out);
}

KhzSheetStatus khz_rational_compare(KhzRational a, KhzRational b, int *cmp)
{
    int order;

    if (cmp == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }
    if (!khz_rational_is_valid(a) || !khz_rational_is_valid(b)) {
        return KHZ_SHEET_ERR_RANGE;
    }

    if (a.den == b.den) {
        *cmp = a.num < b.num ? -1 : (a.num > b.num ? 1 : 0);
        return KHZ_SHEET_OK;
    }

    if ((a.num < (int64_t)0) != (b.num < (int64_t)0)) {
        *cmp = a.num < b.num ? -1 : 1;
        return KHZ_SHEET_OK;
    }

    order = khz_fraction_compare_u64(khz_abs_u64(a.num), (uint64_t)a.den,
                                     khz_abs_u64(b.num), (uint64_t)b.den);

    /* Negating both operands reverses their order. */
    if (a.num < (int64_t)0) {
        order = -order;
    }

    *cmp = order;
    return KHZ_SHEET_OK;
}

int khz_rational_is_integer(KhzRational a)
{
    if (!khz_rational_is_valid(a)) {
        return 0;
    }
    return a.den == (int64_t)1 ? 1 : 0;
}

KhzSheetStatus khz_rational_to_i64(KhzRational a, int64_t *out)
{
    if (out == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }
    if (!khz_rational_is_valid(a)) {
        return KHZ_SHEET_ERR_RANGE;
    }
    if (a.den != (int64_t)1) {
        return KHZ_SHEET_ERR_RANGE;
    }

    *out = a.num;
    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_rational_to_double(KhzRational a, double *out)
{
    if (out == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }
    if (!khz_rational_is_valid(a)) {
        return KHZ_SHEET_ERR_RANGE;
    }

    *out = (double)a.num / (double)a.den;
    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_rational_format(KhzRational a, char *out, size_t capacity, size_t *written)
{
    char buffer[48];
    int length;
    size_t needed;

    if (out == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }
    if (!khz_rational_is_valid(a)) {
        return KHZ_SHEET_ERR_RANGE;
    }

    if (a.den == (int64_t)1) {
        length = snprintf(buffer, sizeof buffer, "%" PRId64, a.num);
    } else {
        length = snprintf(buffer, sizeof buffer, "%" PRId64 "/%" PRId64, a.num, a.den);
    }

    if (length < 0) {
        return KHZ_SHEET_ERR_FORMAT;
    }

    needed = (size_t)length;
    if (needed + (size_t)1 > capacity) {
        return KHZ_SHEET_ERR_RANGE;
    }

    {
        size_t i;
        for (i = (size_t)0; i < needed; ++i) {
            out[i] = buffer[i];
        }
        out[needed] = '\0';
    }

    if (written != NULL) {
        *written = needed;
    }

    return KHZ_SHEET_OK;
}

uint64_t khz_rational_hash(KhzRational a)
{
    unsigned char encoded[16];

    khz_store_le64(encoded, (uint64_t)a.num);
    khz_store_le64(encoded + 8, (uint64_t)a.den);

    return khz_fnv1a64(encoded, sizeof encoded);
}
