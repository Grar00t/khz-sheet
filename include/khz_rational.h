#ifndef KHZ_RATIONAL_H
#define KHZ_RATIONAL_H

#include <stddef.h>
#include <stdint.h>

#include "khz_status.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Exact rational. Invariants held by every function in this header:
     den > 0
     gcd(|num|, den) == 1
     num == 0 implies den == 1
     neither field is INT64_MIN

   INT64_MIN is excluded on purpose: its negation is not representable, so a
   value that carried it could not be normalised or negated without a silent
   wrap. It is refused at construction instead.

   Nothing here rounds. Every operation that cannot be represented exactly
   returns KHZ_SHEET_ERR_OVERFLOW and leaves *out untouched. There is no
   fallback to double: a spreadsheet that answers 0.30000000000000004 has
   answered a different question. */
typedef struct KhzRational {
    int64_t num;
    int64_t den;
} KhzRational;

/* Exact zero: 0/1. */
KhzRational khz_rational_zero(void);

/* Exact one: 1/1. */
KhzRational khz_rational_one(void);

/* Normalises num/den into *out. den == 0 is KHZ_SHEET_ERR_DIVZERO. */
KhzSheetStatus khz_rational_make(int64_t num, int64_t den, KhzRational *out);

KhzSheetStatus khz_rational_from_i64(int64_t value, KhzRational *out);

KhzSheetStatus khz_rational_add(KhzRational a, KhzRational b, KhzRational *out);
KhzSheetStatus khz_rational_sub(KhzRational a, KhzRational b, KhzRational *out);
KhzSheetStatus khz_rational_mul(KhzRational a, KhzRational b, KhzRational *out);
KhzSheetStatus khz_rational_div(KhzRational a, KhzRational b, KhzRational *out);
KhzSheetStatus khz_rational_neg(KhzRational a, KhzRational *out);
KhzSheetStatus khz_rational_abs(KhzRational a, KhzRational *out);

/* *cmp receives -1, 0 or 1. Comparison is exact and does not form potentially
   overflowing cross-products; every pair of valid KhzRational values is
   orderable. */
KhzSheetStatus khz_rational_compare(KhzRational a, KhzRational b, int *cmp);

/* 1 when den == 1, else 0. A malformed input reports 0. */
int khz_rational_is_integer(KhzRational a);

/* Exact only. A non-integer value is KHZ_SHEET_ERR_RANGE. */
KhzSheetStatus khz_rational_to_i64(KhzRational a, int64_t *out);

/* Lossy by construction, and named so. Provided for display and for interop
   with formats that cannot carry a rational. Never used internally. */
KhzSheetStatus khz_rational_to_double(KhzRational a, double *out);

/* Writes "n" for an integer and "n/d" otherwise, NUL terminated.
   *written receives the length excluding the terminator.
   A buffer too small is KHZ_SHEET_ERR_RANGE and nothing is written. */
KhzSheetStatus khz_rational_format(KhzRational a, char *out, size_t capacity, size_t *written);

/* Stable across runs and machines: FNV-1a over the canonical little-endian
   encoding of the normalised pair. Equal values always hash equal. */
uint64_t khz_rational_hash(KhzRational a);

/* 1 when every invariant above holds. */
int khz_rational_is_valid(KhzRational a);

#ifdef __cplusplus
}
#endif

#endif /* KHZ_RATIONAL_H */
