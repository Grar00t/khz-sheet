#include "khz_rational.h"
#include "khz_simd.h"
#include <stdio.h>
#define CHECK(c) do { ++checks; if (!(c)) { \
    fprintf(stderr, "FAIL line %d: %s\n", __LINE__, #c); ++failures; } } while (0)
static int same(KhzRational a, KhzRational b) { return a.num == b.num && a.den == b.den; }
static uint64_t next(uint64_t *state)
{
    *state ^= *state >> 12; *state ^= *state << 25; *state ^= *state >> 27;
    return *state * UINT64_C(2685821657736338717);
}
int main(void)
{
    int checks = 0, failures = 0;
    KhzRational out = {17, 19}, maxhalf = {INT64_MAX, 2}, negative = {-INT64_MAX, 2};
    CHECK(khz_rational_add(maxhalf, maxhalf, &out) == KHZ_SHEET_OK);
    CHECK(same(out, (KhzRational){INT64_MAX, 1}));
    CHECK(khz_rational_add(negative, negative, &out) == KHZ_SHEET_OK);
    CHECK(same(out, (KhzRational){-INT64_MAX, 1}));
    CHECK(khz_rational_add(negative, (KhzRational){-1, 2}, &out) == KHZ_SHEET_OK);
    CHECK(same(out, (KhzRational){-INT64_C(4611686018427387904), 1}));
    CHECK(khz_rational_add((KhzRational){1, INT64_C(6000000000000000000)},
                          (KhzRational){1, INT64_C(4000000000000000000)}, &out) == KHZ_SHEET_OK);
    CHECK(same(out, (KhzRational){1, INT64_C(2400000000000000000)}));
    out = (KhzRational){17, 19};
    CHECK(khz_rational_add((KhzRational){INT64_MAX, 1}, (KhzRational){1, 1}, &out) == KHZ_SHEET_ERR_OVERFLOW);
    CHECK(same(out, (KhzRational){17, 19}));
    CHECK(khz_rational_make(INT64_MIN, 1, &out) == KHZ_SHEET_ERR_OVERFLOW);
    {
        int64_t values[] = {INT64_MAX, -INT64_MAX, 0, 0, 1, 0, 0, 0};
        int64_t cancel[] = {INT64_MAX, INT64_MAX, -INT64_MAX};
        int64_t scaled[] = {INT64_MAX, INT64_MAX};
        int64_t minscaled[] = {INT64_MIN};
        int64_t total = 71;
        CHECK(khz_simd_sum_i64(values, 8, &total) == KHZ_SHEET_OK && total == 1);
        CHECK(khz_simd_sum_i64(cancel, 3, &total) == KHZ_SHEET_OK && total == INT64_MAX);
        CHECK(khz_simd_sum_scaled(scaled, 2, 2, &out) == KHZ_SHEET_OK);
        CHECK(same(out, (KhzRational){INT64_MAX, 1}));
        CHECK(khz_simd_sum_scaled(minscaled, 1, 2, &out) == KHZ_SHEET_OK);
        CHECK(same(out, (KhzRational){-INT64_C(4611686018427387904), 1}));
        total = 71;
        CHECK(khz_simd_sum_i64(scaled, 2, &total) == KHZ_SHEET_ERR_OVERFLOW && total == 71);
        CHECK(khz_simd_sum_i64(NULL, 0, &total) == KHZ_SHEET_OK && total == 0);
        CHECK(khz_simd_sum_i64(NULL, 1, &total) == KHZ_SHEET_ERR_NULL);
        CHECK(khz_simd_sum_scaled(values, 8, 0, &out) == KHZ_SHEET_ERR_RANGE);
    }
    uint64_t state = UINT64_C(0x524154494f4e414c);
    for (int i = 0; i < 10000; ++i) {
        KhzRational a, b, x, y, zero = {0, 1}, one = {1, 1};
        int ab = 0, ba = 0;
        int64_t an = (int64_t)(next(&state) % 2000001) - 1000000;
        int64_t bn = (int64_t)(next(&state) % 2000001) - 1000000;
        int64_t ad = (int64_t)(next(&state) % 10000) + 1;
        int64_t bd = (int64_t)(next(&state) % 10000) + 1;
        CHECK(khz_rational_make(an, ad, &a) == KHZ_SHEET_OK);
        CHECK(khz_rational_make(bn, bd, &b) == KHZ_SHEET_OK);
        CHECK(khz_rational_add(a, zero, &x) == KHZ_SHEET_OK && same(x, a));
        CHECK(khz_rational_sub(a, a, &x) == KHZ_SHEET_OK && same(x, zero));
        CHECK(khz_rational_mul(a, one, &x) == KHZ_SHEET_OK && same(x, a));
        CHECK(khz_rational_div(a, one, &x) == KHZ_SHEET_OK && same(x, a));
        CHECK(khz_rational_mul(a, zero, &x) == KHZ_SHEET_OK && same(x, zero));
        CHECK(khz_rational_add(a, b, &x) == KHZ_SHEET_OK);
        CHECK(khz_rational_add(b, a, &y) == KHZ_SHEET_OK && same(x, y));
        CHECK(khz_rational_sub(x, b, &y) == KHZ_SHEET_OK && same(y, a));
        CHECK(khz_rational_mul(a, b, &x) == KHZ_SHEET_OK);
        CHECK(khz_rational_mul(b, a, &y) == KHZ_SHEET_OK && same(x, y));
        CHECK(khz_rational_compare(a, b, &ab) == KHZ_SHEET_OK);
        CHECK(khz_rational_compare(b, a, &ba) == KHZ_SHEET_OK && ab == -ba);
        CHECK(khz_rational_make(an * 7, ad * 7, &y) == KHZ_SHEET_OK && same(y, a));
        CHECK(khz_rational_hash(a) == khz_rational_hash(y));
        if (an != 0) CHECK(khz_rational_div(a, a, &x) == KHZ_SHEET_OK && same(x, one));
    }
    printf("checks=%d failures=%d kernel=%s\n", checks, failures,
           khz_simd_kernel_name(khz_simd_kernel()));
    return failures ? 1 : 0;
}
