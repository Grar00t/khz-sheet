#ifndef KHZ_LEDGER_H
#define KHZ_LEDGER_H

#include <stddef.h>
#include <stdint.h>

#include "khz_arena.h"
#include "khz_cell.h"
#include "khz_hash.h"
#include "khz_sheet.h"
#include "khz_status.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Durable append-only record of cell commits.

   Why this exists: khz_sheet_verify_chain can only replay the cells that are
   currently in the grid, in insertion order. That is the true commit order
   only while every cell was written exactly once. Once a cell is rewritten,
   the event order is no longer recoverable from the grid at all - the earlier
   payload is simply gone. The ledger stores each commit as it happens, so the
   chain remains verifiable across rewrites.

   What it does not do: it stores hashes, not payloads. It can prove that the
   recorded links form an unbroken chain and that no recorded hash was altered
   or reordered. It cannot prove that a hash corresponds to a particular cell
   value, because that value is not stored here. Reconstructing values from the
   ledger alone is impossible by design, and this header will not pretend
   otherwise.

   sqlite3 handles are the one documented exception to the no-heap rule: the
   library allocates internally and there is no supported way to redirect it
   into an arena. Everything this file allocates itself comes from the arena. */

#define KHZ_LEDGER_TABLE "commits"

/* Longest path this adapter will accept, including the terminator. A path is
   copied into the arena at open time so the caller's buffer need not outlive
   the call. */
#define KHZ_LEDGER_MAX_PATH ((size_t)4096)

typedef struct KhzLedger {
    KhzArena     *arena;
    void         *db;          /* sqlite3 *      */
    void         *append_stmt; /* sqlite3_stmt * */
    const char   *path;        /* arena owned    */
    unsigned char head[KHZ_SHA256_DIGEST_BYTES];
    uint64_t      appended;
    uint64_t      rows;
    int           last_rc;     /* raw sqlite result code of the last failure */
    int           opened;
} KhzLedger;

/* Binds the ledger to an arena. Does not touch the filesystem. */
KhzSheetStatus khz_ledger_init(KhzLedger *ledger, KhzArena *arena);

/* Opens or creates the database at path, creates the schema if absent, and
   loads the existing row count and chain head.

   Reopening an existing ledger resumes its chain: the head becomes the hash of
   the last recorded commit, so the next append must chain onto it. */
KhzSheetStatus khz_ledger_open(KhzLedger *ledger, const char *path);

/* Closes the database and finalises the prepared statement. Safe on an
   unopened ledger. The arena is not touched: bytes taken from it stay taken
   until the arena itself is reset. */
void khz_ledger_close(KhzLedger *ledger);

/* Records one committed cell.

   The cell's stored proof must equal digest(cell, ledger head). A cell whose
   proof does not chain onto this ledger's head is rejected with
   KHZ_SHEET_ERR_FORMAT and nothing is written - the ledger will not record a
   link it cannot itself derive. On success the head advances to the cell's
   proof.

   The timestamp is recorded for operators and is deliberately not part of any
   digest, so the chain stays reproducible on a machine with a different
   clock. */
KhzSheetStatus khz_ledger_append_cell(KhzLedger *ledger, const KhzCell *cell);

/* Appends every cell currently in the sheet, in grid order, and then checks
   that the resulting head equals the sheet's own head. Intended for the
   single-pass case where each cell has been committed exactly once; a sheet
   containing rewrites will fail with KHZ_SHEET_ERR_FORMAT on the first cell
   whose proof does not chain, which is the honest outcome rather than a
   fabricated ordering. */
KhzSheetStatus khz_ledger_append_sheet(KhzLedger *ledger, const KhzSheet *sheet);

/* Replays every recorded row in id order and checks that each row's prev_hash
   equals the previous row's hash, that the first chains onto the 32 zero bytes
   of genesis, and that the last equals the in-memory head.

   *checked receives the number of rows examined. *failed_id receives the row
   id of the first break, or -1 when the chain is whole. Both are optional. */
KhzSheetStatus khz_ledger_verify_chain(KhzLedger *ledger, uint64_t *checked,
                                       int64_t *failed_id);

/* Loads the whole chain into arena memory in id order, newest last. The array
   is arena owned; take a mark before calling and release it afterwards if the
   bytes are wanted back. */
KhzSheetStatus khz_ledger_load_chain(KhzLedger *ledger,
                                     unsigned char (**hashes)[KHZ_SHA256_DIGEST_BYTES],
                                     size_t *count);

/* Number of commits recorded for one coordinate. Answers "how many times has
   this cell changed", which the grid cannot answer after a rewrite. */
KhzSheetStatus khz_ledger_cell_history_count(KhzLedger *ledger, uint32_t col,
                                             uint32_t row, uint64_t *count);

KhzSheetStatus khz_ledger_head(const KhzLedger *ledger,
                               unsigned char out[KHZ_SHA256_DIGEST_BYTES]);
KhzSheetStatus khz_ledger_head_hex(const KhzLedger *ledger,
                                   char out[KHZ_SHA256_HEX_BYTES]);

uint64_t khz_ledger_rows(const KhzLedger *ledger);
uint64_t khz_ledger_appended(const KhzLedger *ledger);

/* Human-readable text for the last sqlite failure, or "" when the last
   operation succeeded. Owned by sqlite; valid until the next ledger call. */
const char *khz_ledger_last_error(const KhzLedger *ledger);

/* 1 when this translation unit was compiled with sqlite3 available. When 0,
   every function above returns KHZ_SHEET_ERR_UNSUPPORTED and touches
   nothing. */
int khz_ledger_available(void);

#ifdef __cplusplus
}
#endif

#endif /* KHZ_LEDGER_H */
