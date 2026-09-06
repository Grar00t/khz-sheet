#ifndef KHZ_SHEET_H
#define KHZ_SHEET_H

#include <stddef.h>
#include <stdint.h>

#include "khz_arena.h"
#include "khz_cell.h"
#include "khz_grid.h"
#include "khz_hash.h"
#include "khz_rational.h"
#include "khz_simd.h"
#include "khz_status.h"

#ifdef __cplusplus
extern "C" {
#endif

#define KHZ_SHEET_DEFAULT_CELLS ((size_t)1 << 16)

/* One sheet owns exactly one arena. Every byte the sheet uses - slots, cells,
   edge nodes, copied text, scratch for a topological sort - comes out of that
   arena. There is no malloc below this line, so there is no leak to find and
   no free order to get wrong: khz_sheet_destroy returns the whole reservation
   in one call. */
typedef struct KhzSheet {
    KhzArena      arena;
    KhzGrid       grid;
    KhzDepGraph   deps;
    unsigned char proof[KHZ_SHA256_DIGEST_BYTES]; /* chain head */
    uint64_t      commits;
    int           initialised;
} KhzSheet;

/* arena_bytes is the whole budget. cell_capacity is the ceiling on populated
   cells and cannot be raised afterwards. */
KhzSheetStatus khz_sheet_init(KhzSheet *sheet, size_t arena_bytes, size_t cell_capacity);
KhzSheetStatus khz_sheet_init_default(KhzSheet *sheet);
void khz_sheet_destroy(KhzSheet *sheet);

/* Each setter copies any text into the arena, commits the cell onto the chain
   head, and advances the head. A failed setter advances nothing. */
KhzSheetStatus khz_sheet_set_rational(KhzSheet *sheet, uint32_t col, uint32_t row,
                                     KhzRational value);
KhzSheetStatus khz_sheet_set_i64(KhzSheet *sheet, uint32_t col, uint32_t row,
                                int64_t value);
KhzSheetStatus khz_sheet_set_text(KhzSheet *sheet, uint32_t col, uint32_t row,
                                 const char *text, size_t len);
KhzSheetStatus khz_sheet_set_bool(KhzSheet *sheet, uint32_t col, uint32_t row,
                                 int value);
KhzSheetStatus khz_sheet_set_error(KhzSheet *sheet, uint32_t col, uint32_t row,
                                  KhzCellError error);
KhzSheetStatus khz_sheet_set_formula(KhzSheet *sheet, uint32_t col, uint32_t row,
                                    const char *formula, size_t len);

/* Borrowed pointer into the arena, valid until the sheet is reset or
   destroyed. KHZ_SHEET_ERR_MISSING for a blank coordinate. */
KhzSheetStatus khz_sheet_get(const KhzSheet *sheet, uint32_t col, uint32_t row,
                            const KhzCell **cell);

KhzSheetStatus khz_sheet_declare_dependency(KhzSheet *sheet,
                                            uint32_t from_col, uint32_t from_row,
                                            uint32_t to_col, uint32_t to_row);

/* order must hold khz_sheet_cell_count entries. KHZ_SHEET_ERR_CYCLE on a
   circular reference. */
KhzSheetStatus khz_sheet_evaluation_order(KhzSheet *sheet, size_t *order,
                                          size_t capacity, size_t *count);

/* SUM and AVERAGE over an inclusive rectangle, exact.

   Only KHZ_CELL_RATIONAL cells contribute. Text and blanks are skipped, which
   is the spreadsheet rule. An error cell inside the range returns
   KHZ_SHEET_ERR_TYPE: the error must propagate, and this layer has nowhere to
   put it, so it refuses instead of dropping it.

   When every contributing cell shares a denominator the numerators are gathered
   into arena scratch and summed through the SIMD kernel; otherwise the fold is
   scalar. Both paths produce the same rational. */
KhzSheetStatus khz_sheet_sum(KhzSheet *sheet,
                             uint32_t col0, uint32_t row0,
                             uint32_t col1, uint32_t row1,
                             KhzRational *out, size_t *counted);
KhzSheetStatus khz_sheet_avg(KhzSheet *sheet,
                             uint32_t col0, uint32_t row0,
                             uint32_t col1, uint32_t row1,
                             KhzRational *out, size_t *counted);

/* Recomputes every cell digest in insertion order and checks the chain head.
   KHZ_SHEET_OK means no cell and no link has been altered since it was
   committed. *failed_index receives the first mismatching cell when it is not
   OK, and is left alone otherwise. */
KhzSheetStatus khz_sheet_verify_chain(const KhzSheet *sheet, size_t *failed_index);

KhzSheetStatus khz_sheet_proof_hex(const KhzSheet *sheet, char out[KHZ_SHA256_HEX_BYTES]);

size_t   khz_sheet_cell_count(const KhzSheet *sheet);
uint64_t khz_sheet_commits(const KhzSheet *sheet);
KhzArena *khz_sheet_arena(KhzSheet *sheet);

/* Arena mark and release, exposed so a caller can bound a speculative batch
   and give the bytes back. Releasing below a mark taken before cells were
   added invalidates those cells; the grid is not rewound with the arena, so
   this is for scratch use, not for undo. */
size_t khz_sheet_mark(const KhzSheet *sheet);
KhzSheetStatus khz_sheet_release(KhzSheet *sheet, size_t mark);

/* Returns the number of failed checks across the arena, hash, rational, grid
   and SIMD layers. Zero means every compiled-in known-answer test matched. */
int khz_sheet_selftest(void);

#ifdef __cplusplus
}
#endif

#endif /* KHZ_SHEET_H */
