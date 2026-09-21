#ifndef KHZ_CELL_H
#define KHZ_CELL_H

#include <stddef.h>
#include <stdint.h>

#include "khz_hash.h"
#include "khz_rational.h"
#include "khz_status.h"

#ifdef __cplusplus
extern "C" {
#endif

#define KHZ_CELL_PROOF_BYTES ((size_t)KHZ_SHA256_DIGEST_BYTES)

/* Bumped whenever the digest pre-image changes. A proof produced under a
   different tag is not comparable to one produced under this tag, and mixing
   them silently would make the chain meaningless.

   KHZCELL2 excludes transient cache-state bits (currently DIRTY) from the
   digest. Formula invalidation is not a cell commit: dependencies may mark a
   cached formula stale between commits, and that normal scheduler state must
   not look like proof tampering. */
#define KHZ_CELL_PROOF_TAG "KHZCELL2"
#define KHZ_CELL_PROOF_TAG_BYTES ((size_t)8)

typedef enum KhzCellKind {
    KHZ_CELL_EMPTY    = 0,
    KHZ_CELL_RATIONAL = 1,
    KHZ_CELL_TEXT     = 2,
    KHZ_CELL_BOOL     = 3,
    KHZ_CELL_ERROR    = 4,
    KHZ_CELL_FORMULA  = 5
} KhzCellKind;

typedef enum KhzCellError {
    KHZ_CELL_ERROR_NONE   = 0,
    KHZ_CELL_ERROR_NULL   = 1,
    KHZ_CELL_ERROR_DIV0   = 2,
    KHZ_CELL_ERROR_VALUE  = 3,
    KHZ_CELL_ERROR_REF    = 4,
    KHZ_CELL_ERROR_NAME   = 5,
    KHZ_CELL_ERROR_NUM    = 6,
    KHZ_CELL_ERROR_NA     = 7
} KhzCellError;

/* Scheduler/cache state, not committed semantic state. Recalculation clears
   it; dependency invalidation sets it. Because either may happen without a
   cell edit event, this bit is deliberately excluded from the proof pre-image. */
#define KHZ_CELL_FLAG_DIRTY 0x00000001u

/* Flags included in the proof pre-image. Add future semantic flags here. A
   flag that can change without khz_cell_commit must not be included. */
#define KHZ_CELL_PROOF_FLAGS_MASK (~(uint32_t)KHZ_CELL_FLAG_DIRTY)

typedef struct KhzCell {
    uint32_t      col;
    uint32_t      row;
    uint32_t      kind;
    uint32_t      error;
    KhzRational   value;
    const char   *text;
    const char   *formula;
    uint32_t      text_len;
    uint32_t      formula_len;
    uint32_t      bool_value;
    uint32_t      flags;
    uint64_t      revision;
    unsigned char proof[KHZ_SHA256_DIGEST_BYTES];
    unsigned char reserved[24];
} KhzCell;

_Static_assert(sizeof(KhzCell) <= 128, "KhzCell must stay within two cache lines");
_Static_assert(sizeof(KhzCell) % 8 == 0, "KhzCell must be word sized");

uint64_t khz_cell_key(uint32_t col, uint32_t row);
KhzSheetStatus khz_cell_key_split(uint64_t key, uint32_t *col, uint32_t *row);

KhzSheetStatus khz_cell_init_empty(KhzCell *cell, uint32_t col, uint32_t row);

KhzSheetStatus khz_cell_set_empty(KhzCell *cell);
KhzSheetStatus khz_cell_set_rational(KhzCell *cell, KhzRational value);
KhzSheetStatus khz_cell_set_text(KhzCell *cell, const char *text, uint32_t len);
KhzSheetStatus khz_cell_set_bool(KhzCell *cell, int value);
KhzSheetStatus khz_cell_set_error(KhzCell *cell, KhzCellError error);
KhzSheetStatus khz_cell_set_formula(KhzCell *cell, const char *formula, uint32_t len);

/* Digest of this cell's committed semantic state chained onto prev.

   Pre-image, in order, with every integer little-endian:
     prev[32] | tag[8] | col | row | kind | error | num | den | text_len |
     formula_len | bool_value | proof_flags | revision | text bytes |
     formula bytes

   proof_flags is cell->flags & KHZ_CELL_PROOF_FLAGS_MASK. DIRTY is an
   evaluation-cache state and is intentionally omitted. The lengths precede
   variable-length fields, so no pair of distinct cells shares a pre-image by
   concatenation. */
KhzSheetStatus khz_cell_digest(const KhzCell *cell,
                               const unsigned char prev[KHZ_SHA256_DIGEST_BYTES],
                               unsigned char out[KHZ_SHA256_DIGEST_BYTES]);

KhzSheetStatus khz_cell_commit(KhzCell *cell,
                               const unsigned char prev[KHZ_SHA256_DIGEST_BYTES]);

KhzSheetStatus khz_cell_verify(const KhzCell *cell,
                               const unsigned char prev[KHZ_SHA256_DIGEST_BYTES]);

const char *khz_cell_kind_name(KhzCellKind kind);
const char *khz_cell_error_name(KhzCellError error);

#ifdef __cplusplus
}
#endif

#endif /* KHZ_CELL_H */
