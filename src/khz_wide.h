#ifndef KHZ_WIDE_H
#define KHZ_WIDE_H
#include <stdint.h>
/* Internal unsigned two-limb arithmetic. No compiler extensions or ABI surface.
   Callers prove additions fit 128 bits and divisors are in [1, INT64_MAX]. */
typedef struct KhzWide { uint64_t hi, lo; } KhzWide;
static inline KhzWide khz_wide_mul(uint64_t a, uint64_t b)
{
    const uint64_t mask = UINT64_C(0xffffffff);
    uint64_t a0 = a & mask, a1 = a >> 32, b0 = b & mask, b1 = b >> 32;
    uint64_t t = a0 * b0, low = t & mask, carry = t >> 32;
    t = a1 * b0 + carry;
    carry = t >> 32;
    t = a0 * b1 + (t & mask);
    return (KhzWide){a1 * b1 + carry + (t >> 32), (t << 32) | low};
}
static inline int khz_wide_cmp(KhzWide a, KhzWide b)
{
    if (a.hi != b.hi) return a.hi < b.hi ? -1 : 1;
    return a.lo < b.lo ? -1 : (a.lo > b.lo ? 1 : 0);
}
static inline KhzWide khz_wide_add(KhzWide a, KhzWide b)
{
    uint64_t low = a.lo + b.lo;
    return (KhzWide){a.hi + b.hi + (uint64_t)(low < a.lo), low};
}
static inline KhzWide khz_wide_sub(KhzWide a, KhzWide b)
{
    return (KhzWide){a.hi - b.hi - (uint64_t)(a.lo < b.lo), a.lo - b.lo};
}
static inline uint64_t khz_wide_div(KhzWide n, uint64_t d, KhzWide *q)
{
    uint64_t rem = 0;
    *q = (KhzWide){0, 0};
    if (n.hi == 0) { q->lo = n.lo / d; return n.lo % d; }
    for (int bit = 127; bit >= 0; --bit) {
        uint64_t digit = bit >= 64 ? (n.hi >> (bit - 64)) & 1u : (n.lo >> bit) & 1u;
        rem = (rem << 1) | digit;
        if (rem >= d) {
            rem -= d;
            if (bit >= 64) q->hi |= UINT64_C(1) << (bit - 64);
            else q->lo |= UINT64_C(1) << bit;
        }
    }
    return rem;
}
static inline uint64_t khz_wide_magnitude(int64_t n)
{
    return n < 0 ? UINT64_C(0) - (uint64_t)n : (uint64_t)n;
}
static inline uint64_t khz_wide_gcd(uint64_t a, uint64_t b)
{
    while (b) { uint64_t r = a % b; a = b; b = r; }
    return a;
}
static inline void khz_wide_signed_add(KhzWide *a, int *negative, KhzWide b, int bnegative)
{
    if (*negative == bnegative) *a = khz_wide_add(*a, b);
    else if (khz_wide_cmp(*a, b) >= 0) *a = khz_wide_sub(*a, b);
    else { *a = khz_wide_sub(b, *a); *negative = bnegative; }
    if (a->hi == 0 && a->lo == 0) *negative = 0;
}
#endif
