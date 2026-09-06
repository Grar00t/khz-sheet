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
   them silently would make the chain meaningless. */
#define KHZ_CELL_PROOF_TAG "KHZCELL1"
#define KHZ_CELL_PROOF_TAG_BYTES ((size_t)8)

typedef enum KhzCellKind {
    KHZ_CELL_EMPTY    = 0,
    KHZ_CELL_RATIONAL = 1,
    KHZ_CELL_TEXT     = 2,
    KHZ_CELL_BOOL     = 3,
    KHZ_CELL_ERROR    = 4,
    KHZ_CELL_FORMULA  = 5
} KhzCellKind;

/* The error literals the file format defines. Nothing else is representable,
   so nothing else can be invented at read time. */
typedef enum KhzCellError {
    KHZ_CELL_ERROR_NONE   = 0,
    KHZ_CELL_ERROR_NULL   = 1, /* #NULL!   */
    KHZ_CELL_ERROR_DIV0   = 2, /* #DIV/0!  */
    KHZ_CELL_ERROR_VALUE  = 3, /* #VALUE!  */
    KHZ_CELL_ERROR_REF    = 4, /* #REF!    */
    KHZ_CELL_ERROR_NAME   = 5, /* #NAME?   */
    KHZ_CELL_ERROR_NUM    = 6, /* #NUM!    */
    KHZ_CELL_ERROR_NA     = 7  /* #N/A     */
} KhzCellError;

/* Set when the cached value is known to be behind the formula. Recalculation
   clears it; nothing else may. */
#define KHZ_CELL_FLAG_DIRTY 0x00000001u

/* One cell. Fields are ordered so the hot triple (kind, value) shares the
   first 32 bytes, and the struct is a whole number of 8-byte words so an
   array of cells taken from a 64-byte-aligned arena block stays aligned.

   text and formula point into the arena that owns the cell. A cell never owns
   heap memory, so there is nothing to free and nothing to leak. */
typedef struct KhzCell {
    uint32_t      col;
    uint32_t      row;
    uint32_t      kind;          /* KhzCellKind  */
    uint32_t      error;         /* KhzCellError */
    KhzRational   value;         /* KHZ_CELL_RATIONAL */
    const char   *text;          /* KHZ_CELL_TEXT, arena owned, not NUL required */
    const char   *formula;       /* KHZ_CELL_FORMULA, arena owned */
    uint32_t      text_len;
    uint32_t      formula_len;
    uint32_t      bool_value;    /* KHZ_CELL_BOOL, 0 or 1 */
    uint32_t      flags;
    uint64_t      revision;
    unsigned char proof[KHZ_SHA256_DIGEST_BYTES];
    unsigned char reserved[24];
} KhzCell;

_Static_assert(sizeof(KhzCell) <= 128, "KhzCell must stay within two cache lines");
_Static_assert(sizeof(KhzCell) % 8 == 0, "KhzCell must be word sized");

/* Packs a coordinate into one key: column in the high 32 bits, row in the low
   32. Injective over the whole legal grid, so no collision is possible from
   the packing itself. */
uint64_t khz_cell_key(uint32_t col, uint32_t row);
KhzSheetStatus khz_cell_key_split(uint64_t key, uint32_t *col, uint32_t *row);

/* Zeroes the cell, sets the coordinate and leaves kind EMPTY with a zero
   proof. A zero proof means uncommitted, and is distinguishable from any real
   digest only by convention - callers must check revision, which is 0 only
   before the first commit. */
KhzSheetStatus khz_cell_init_empty(KhzCell *cell, uint32_t col, uint32_t row);

/* Each setter clears the fields belonging to the other kinds, so a cell can
   never carry a stale payload from a kind it no longer is. None of them
   commits; the proof is stale until khz_cell_commit runs. */
KhzSheetStatus khz_cell_set_empty(KhzCell *cell);
KhzSheetStatus khz_cell_set_rational(KhzCell *cell, KhzRational value);
KhzSheetStatus khz_cell_set_text(KhzCell *cell, const char *text, uint32_t len);
KhzSheetStatus khz_cell_set_bool(KhzCell *cell, int value);
KhzSheetStatus khz_cell_set_error(KhzCell *cell, KhzCellError error);
KhzSheetStatus khz_cell_set_formula(KhzCell *cell, const char *formula, uint32_t len);

/* Digest of this cell's committed state chained onto prev.

   Pre-image, in order, with every integer little-endian:
     prev[32] | tag[8] | col | row | kind | error | num | den | text_len |
     formula_len | bool_value | flags | revision | text bytes | formula bytes

   The lengths precede the variable-length fields, so no pair of distinct cells
   shares a pre-image by concatenation. */
KhzSheetStatus khz_cell_digest(const KhzCell *cell,
                               const unsigned char prev[KHZ_SHA256_DIGEST_BYTES],
                               unsigned char out[KHZ_SHA256_DIGEST_BYTES]);

/* Increments revision, then writes digest(cell, prev) into cell->proof.
   Revision is part of the pre-image, so committing the same payload twice
   yields two different proofs - which is the point: the chain records events,
   not just states. */
KhzSheetStatus khz_cell_commit(KhzCell *cell,
                               const unsigned char prev[KHZ_SHA256_DIGEST_BYTES]);

/* Recomputes the digest and compares it in constant time to the stored proof.
   KHZ_SHEET_OK means the cell matches its own proof under prev. */
KhzSheetStatus khz_cell_verify(const KhzCell *cell,
                               const unsigned char prev[KHZ_SHA256_DIGEST_BYTES]);

const char *khz_cell_kind_name(KhzCellKind kind);
const char *khz_cell_error_name(KhzCellError error);

#ifdef __cplusplus
}
#endif

#endif /* KHZ_CELL_H */
