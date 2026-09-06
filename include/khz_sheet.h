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

/* Commit-log bounds. The log is a ring sized from the sheet's cell capacity
   and clamped to this window, because the log must not be allowed to consume
   an arena that was sized for cells. */
#define KHZ_SHEET_COMMIT_LOG_MIN ((size_t)256)
#define KHZ_SHEET_COMMIT_LOG_MAX ((size_t)1 << 16)

/* One commit event.

   key and revision identify which cell was written and which of its writes
   this was. head is the chain head immediately after the commit, so a walk of
   consecutive entries reproduces the link sequence without needing the cell
   payloads. */
typedef struct KhzCommitEntry {
    uint64_t      key;      /* khz_cell_key(col, row) */
    uint64_t      revision; /* cell revision at this commit */
    unsigned char head[KHZ_SHA256_DIGEST_BYTES];
} KhzCommitEntry;

/* The commit event log.

   Why this exists: the chain records events, not states. Once a cell is
   committed twice, its earlier payload is gone and no amount of walking the
   cell array can reconstruct the order in which the links were formed.
   Verification therefore needs the event sequence, and this is it.

   It is a ring, so a long-running sheet cannot grow the log without bound.
   When an entry is overwritten the window start moves and base holds the head
   as it stood before the oldest retained entry, which keeps the retained
   window verifiable on its own terms. dropped counts what fell out, so the
   audit can state the limits of what it checked instead of implying it
   checked everything. */
typedef struct KhzCommitLog {
    KhzCommitEntry *entries;
    size_t          capacity;
    size_t          next;     /* ring write cursor */
    uint64_t        recorded; /* total appends over the sheet's life */
    uint64_t        dropped;  /* entries overwritten and no longer retained */
    unsigned char   base[KHZ_SHA256_DIGEST_BYTES]; /* head before oldest retained */
} KhzCommitLog;

/* One sheet owns exactly one arena. Every byte the sheet uses - slots, cells,
   edge nodes, copied text, the commit log, scratch for a topological sort -
   comes out of that arena. There is no malloc below this line, so there is no
   leak to find and no free order to get wrong: khz_sheet_destroy returns the
   whole reservation in one call. */
typedef struct KhzSheet {
    KhzArena      arena;
    KhzGrid       grid;
    KhzDepGraph   deps;
    KhzCommitLog  log;
    unsigned char proof[KHZ_SHA256_DIGEST_BYTES]; /* chain head */
    uint64_t      commits;
    int           initialised;
} KhzSheet;

/* arena_bytes is the whole budget. cell_capacity is the ceiling on populated
   cells and cannot be raised afterwards; it also sizes the commit log. */
KhzSheetStatus khz_sheet_init(KhzSheet *sheet, size_t arena_bytes, size_t cell_capacity);
KhzSheetStatus khz_sheet_init_default(KhzSheet *sheet);
void khz_sheet_destroy(KhzSheet *sheet);

/* Each setter copies any text into the arena, commits the cell onto the chain
   head, advances the head, and records the event in the commit log. A failed
   setter advances nothing and records nothing. */
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

/* Commits a cell that was modified in place, then records the event.

   Recalculation needs this: a formula cell's value changes but its kind and
   source text must not, so no setter applies. Everything that forms a link in
   the chain goes through here or through a setter, and nothing else may touch
   sheet->proof - otherwise the log and the chain would disagree and the audit
   would be measuring its own bookkeeping rather than the data. */
KhzSheetStatus khz_sheet_commit_in_place(KhzSheet *sheet, KhzCell *cell);

/* Borrowed pointer into the arena, valid until the sheet is reset or
   destroyed. KHZ_SHEET_ERR_MISSING for a blank coordinate. */
KhzSheetStatus khz_sheet_get(const KhzSheet *sheet, uint32_t col, uint32_t row,
                            const KhzCell **cell);

/* Mutable form, for callers that update a cell in place and then commit it.
   KHZ_SHEET_ERR_MISSING for a blank coordinate. */
KhzSheetStatus khz_sheet_get_mutable(KhzSheet *sheet, uint32_t col, uint32_t row,
                                     KhzCell **cell);

KhzSheetStatus khz_sheet_declare_dependency(KhzSheet *sheet,
                                            uint32_t from_col, uint32_t from_row,
                                            uint32_t to_col, uint32_t to_row);

/* order must hold khz_sheet_cell_count entries. KHZ_SHEET_ERR_CYCLE on a
   circular reference. */
KhzSheetStatus khz_sheet_evaluation_order(KhzSheet *sheet, size_t *order,
                                          size_t capacity, size_t *count);

/* Marks a cell and everything reachable from it as needing recalculation.
   Returns the number of cells marked, including the origin when it is a
   formula. A cycle is reported rather than walked. */
KhzSheetStatus khz_sheet_mark_dirty(KhzSheet *sheet, uint32_t col, uint32_t row,
                                    uint64_t *marked);
size_t khz_sheet_dirty_count(const KhzSheet *sheet);

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

/* What an audit was able to establish.

   cells_verified counts entries whose cell still holds the payload that was
   committed, recomputed and matched. superseded counts entries whose cell has
   since been written again: the historical payload no longer exists anywhere,
   so the digest cannot be recomputed and the entry's recorded head is taken as
   the link. dropped counts commits that fell out of the ring window entirely.

   A KHZ_SHEET_OK audit with superseded or dropped above zero is a weaker claim
   than one with both at zero, and these fields exist so the difference is
   visible rather than implied. */
typedef struct KhzChainAudit {
    uint64_t entries_examined;
    uint64_t cells_verified;
    uint64_t superseded;
    uint64_t dropped;
    size_t   failed_entry; /* walk position of the first failure */
} KhzChainAudit;

/* Replays the commit log and checks that the retained link sequence ends at
   the current chain head.

   This replaced an insertion-order replay of the cell array, which was only
   correct while every cell had been written exactly once and which therefore
   reported a failure after any recalculation even though nothing had been
   tampered with. KHZ_SHEET_OK means no retained link and no still-current cell
   payload has been altered since it was committed. */
KhzSheetStatus khz_sheet_verify_chain(const KhzSheet *sheet, size_t *failed_index);
KhzSheetStatus khz_sheet_audit_chain(const KhzSheet *sheet, KhzChainAudit *audit);

KhzSheetStatus khz_sheet_proof_hex(const KhzSheet *sheet, char out[KHZ_SHA256_HEX_BYTES]);

size_t   khz_sheet_cell_count(const KhzSheet *sheet);
uint64_t khz_sheet_commits(const KhzSheet *sheet);
KhzArena *khz_sheet_arena(KhzSheet *sheet);

/* Commit-log state, exposed so a caller can tell how much of the history is
   still retained before trusting an audit. */
size_t   khz_sheet_log_capacity(const KhzSheet *sheet);
uint64_t khz_sheet_log_recorded(const KhzSheet *sheet);
uint64_t khz_sheet_log_dropped(const KhzSheet *sheet);

/* Arena mark and release, exposed so a caller can bound a speculative batch
   and give the bytes back. Releasing below a mark taken before cells were
   added invalidates those cells; the grid is not rewound with the arena, so
   this is for scratch use, not for undo. */
size_t khz_sheet_mark(const KhzSheet *sheet);
KhzSheetStatus khz_sheet_release(KhzSheet *sheet, size_t mark);

const char *khz_sheet_status_name(KhzSheetStatus status);

/* Returns the number of failed checks across the arena, hash, rational, grid
   and SIMD layers. Zero means every compiled-in known-answer test matched. */
int khz_sheet_selftest(void);

#ifdef __cplusplus
}
#endif

#endif /* KHZ_SHEET_H */
