#include "khz_ledger.h"

#include <string.h>
#include <time.h>

#ifdef KHZ_WITH_SQLITE
#include <sqlite3.h>
#endif

int khz_ledger_available(void)
{
#ifdef KHZ_WITH_SQLITE
    return 1;
#else
    return 0;
#endif
}

#ifndef KHZ_WITH_SQLITE

/* Built without sqlite3. Every entry point refuses rather than pretending to
   record anything: a ledger that silently drops commits is worse than no
   ledger, because the caller would believe it had durability it does not
   have. */

KhzSheetStatus khz_ledger_init(KhzLedger *ledger, KhzArena *arena)
{
    if (ledger == NULL || arena == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    memset(ledger, 0, sizeof *ledger);
    ledger->arena = arena;
    return KHZ_SHEET_ERR_UNSUPPORTED;
}

KhzSheetStatus khz_ledger_open(KhzLedger *ledger, const char *path)
{
    (void)ledger;
    (void)path;
    return KHZ_SHEET_ERR_UNSUPPORTED;
}

void khz_ledger_close(KhzLedger *ledger)
{
    (void)ledger;
}

KhzSheetStatus khz_ledger_append_cell(KhzLedger *ledger, const KhzCell *cell)
{
    (void)ledger;
    (void)cell;
    return KHZ_SHEET_ERR_UNSUPPORTED;
}

KhzSheetStatus khz_ledger_append_sheet(KhzLedger *ledger, const KhzSheet *sheet)
{
    (void)ledger;
    (void)sheet;
    return KHZ_SHEET_ERR_UNSUPPORTED;
}

KhzSheetStatus khz_ledger_verify_chain(KhzLedger *ledger, uint64_t *checked,
                                       int64_t *failed_id)
{
    (void)ledger;
    (void)checked;
    (void)failed_id;
    return KHZ_SHEET_ERR_UNSUPPORTED;
}

KhzSheetStatus khz_ledger_load_chain(KhzLedger *ledger,
                                     unsigned char (**hashes)[KHZ_SHA256_DIGEST_BYTES],
                                     size_t *count)
{
    (void)ledger;
    (void)hashes;
    (void)count;
    return KHZ_SHEET_ERR_UNSUPPORTED;
}

KhzSheetStatus khz_ledger_cell_history_count(KhzLedger *ledger, uint32_t col,
                                             uint32_t row, uint64_t *count)
{
    (void)ledger;
    (void)col;
    (void)row;
    (void)count;
    return KHZ_SHEET_ERR_UNSUPPORTED;
}

KhzSheetStatus khz_ledger_head(const KhzLedger *ledger,
                               unsigned char out[KHZ_SHA256_DIGEST_BYTES])
{
    (void)ledger;
    (void)out;
    return KHZ_SHEET_ERR_UNSUPPORTED;
}

KhzSheetStatus khz_ledger_head_hex(const KhzLedger *ledger,
                                   char out[KHZ_SHA256_HEX_BYTES])
{
    (void)ledger;
    (void)out;
    return KHZ_SHEET_ERR_UNSUPPORTED;
}

uint64_t khz_ledger_rows(const KhzLedger *ledger)
{
    (void)ledger;
    return (uint64_t)0;
}

uint64_t khz_ledger_appended(const KhzLedger *ledger)
{
    (void)ledger;
    return (uint64_t)0;
}

const char *khz_ledger_last_error(const KhzLedger *ledger)
{
    (void)ledger;
    return "built without sqlite3";
}

#else /* KHZ_WITH_SQLITE */

/* AUTOINCREMENT rather than a bare INTEGER PRIMARY KEY: plain rowids can be
   reused after a delete, and an append-only chain must never reuse a position.
   UNIQUE(hash) turns a duplicated commit into a constraint failure instead of
   a second identical link - revision is part of the digest pre-image, so two
   genuine commits can never collide here. */
static const char *const KHZ_LEDGER_SCHEMA =
    "CREATE TABLE IF NOT EXISTS commits ("
    "  id        INTEGER PRIMARY KEY AUTOINCREMENT,"
    "  cell_addr INTEGER NOT NULL,"
    "  revision  INTEGER NOT NULL,"
    "  hash      BLOB    NOT NULL UNIQUE,"
    "  prev_hash BLOB    NOT NULL,"
    "  timestamp INTEGER NOT NULL"
    ");"
    "CREATE INDEX IF NOT EXISTS commits_cell_addr ON commits(cell_addr);";

static const char *const KHZ_LEDGER_APPEND_SQL =
    "INSERT INTO commits (cell_addr, revision, hash, prev_hash, timestamp)"
    " VALUES (?1, ?2, ?3, ?4, ?5);";

static KhzSheetStatus khz_ledger_fail(KhzLedger *ledger, int rc, KhzSheetStatus status)
{
    ledger->last_rc = rc;
    return status;
}

static KhzSheetStatus khz_ledger_ready(const KhzLedger *ledger)
{
    if (ledger == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }
    if (ledger->opened == 0 || ledger->db == NULL) {
        return KHZ_SHEET_ERR_STATE;
    }
    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_ledger_init(KhzLedger *ledger, KhzArena *arena)
{
    if (ledger == NULL || arena == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    memset(ledger, 0, sizeof *ledger);
    ledger->arena = arena;

    /* Genesis is 32 zero bytes, the same convention khz_sheet_init uses, so a
       fresh sheet and a fresh ledger agree on the start of the chain. */
    memset(ledger->head, 0, sizeof ledger->head);

    return KHZ_SHEET_OK;
}

static KhzSheetStatus khz_ledger_load_state(KhzLedger *ledger)
{
    sqlite3 *db = (sqlite3 *)ledger->db;
    sqlite3_stmt *stmt = NULL;
    int rc;

    rc = sqlite3_prepare_v2(db, "SELECT COUNT(*) FROM commits;", -1, &stmt, NULL);
    if (rc != SQLITE_OK) {
        return khz_ledger_fail(ledger, rc, KHZ_SHEET_ERR_STATE);
    }

    if (sqlite3_step(stmt) == SQLITE_ROW) {
        sqlite3_int64 rows = sqlite3_column_int64(stmt, 0);

        ledger->rows = rows > 0 ? (uint64_t)rows : (uint64_t)0;
    }
    sqlite3_finalize(stmt);
    stmt = NULL;

    rc = sqlite3_prepare_v2(db,
                            "SELECT hash FROM commits ORDER BY id DESC LIMIT 1;",
                            -1, &stmt, NULL);
    if (rc != SQLITE_OK) {
        return khz_ledger_fail(ledger, rc, KHZ_SHEET_ERR_STATE);
    }

    if (sqlite3_step(stmt) == SQLITE_ROW) {
        const void *blob = sqlite3_column_blob(stmt, 0);
        int len = sqlite3_column_bytes(stmt, 0);

        if (blob == NULL || len != (int)KHZ_SHA256_DIGEST_BYTES) {
            /* A row exists but its hash is not a SHA-256 digest. The file is
               not a ledger this build can extend, and guessing a head would
               corrupt the chain. */
            sqlite3_finalize(stmt);
            return khz_ledger_fail(ledger, SQLITE_CORRUPT, KHZ_SHEET_ERR_FORMAT);
        }

        memcpy(ledger->head, blob, (size_t)KHZ_SHA256_DIGEST_BYTES);
    }

    sqlite3_finalize(stmt);
    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_ledger_open(KhzLedger *ledger, const char *path)
{
    sqlite3 *db = NULL;
    sqlite3_stmt *stmt = NULL;
    char *copy;
    size_t path_len;
    KhzSheetStatus status;
    int rc;

    if (ledger == NULL || path == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }
    if (ledger->arena == NULL) {
        return KHZ_SHEET_ERR_STATE;
    }
    if (ledger->opened != 0) {
        return KHZ_SHEET_ERR_STATE;
    }

    path_len = strlen(path);
    if (path_len == (size_t)0 || path_len + (size_t)1 > KHZ_LEDGER_MAX_PATH) {
        return KHZ_SHEET_ERR_RANGE;
    }

    copy = (char *)khz_arena_alloc(ledger->arena, path_len + (size_t)1);
    if (copy == NULL) {
        return KHZ_SHEET_ERR_MEMORY;
    }
    memcpy(copy, path, path_len + (size_t)1);

    rc = sqlite3_open_v2(copy, &db,
                         SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE, NULL);
    if (rc != SQLITE_OK) {
        if (db != NULL) {
            sqlite3_close(db);
        }
        return khz_ledger_fail(ledger, rc, KHZ_SHEET_ERR_STATE);
    }

    ledger->db = db;
    ledger->path = copy;

    /* FULL synchronous, not NORMAL. A ledger whose last commits can vanish in
       a power cut is not a ledger. */
    rc = sqlite3_exec(db, "PRAGMA journal_mode=WAL;", NULL, NULL, NULL);
    if (rc != SQLITE_OK) {
        khz_ledger_close(ledger);
        return khz_ledger_fail(ledger, rc, KHZ_SHEET_ERR_STATE);
    }

    rc = sqlite3_exec(db, "PRAGMA synchronous=FULL;", NULL, NULL, NULL);
    if (rc != SQLITE_OK) {
        khz_ledger_close(ledger);
        return khz_ledger_fail(ledger, rc, KHZ_SHEET_ERR_STATE);
    }

    rc = sqlite3_exec(db, KHZ_LEDGER_SCHEMA, NULL, NULL, NULL);
    if (rc != SQLITE_OK) {
        khz_ledger_close(ledger);
        return khz_ledger_fail(ledger, rc, KHZ_SHEET_ERR_STATE);
    }

    rc = sqlite3_prepare_v2(db, KHZ_LEDGER_APPEND_SQL, -1, &stmt, NULL);
    if (rc != SQLITE_OK) {
        khz_ledger_close(ledger);
        return khz_ledger_fail(ledger, rc, KHZ_SHEET_ERR_STATE);
    }

    ledger->append_stmt = stmt;
    ledger->opened = 1;

    status = khz_ledger_load_state(ledger);
    if (status != KHZ_SHEET_OK) {
        khz_ledger_close(ledger);
        return status;
    }

    ledger->last_rc = SQLITE_OK;
    return KHZ_SHEET_OK;
}

void khz_ledger_close(KhzLedger *ledger)
{
    if (ledger == NULL) {
        return;
    }

    if (ledger->append_stmt != NULL) {
        sqlite3_finalize((sqlite3_stmt *)ledger->append_stmt);
        ledger->append_stmt = NULL;
    }

    if (ledger->db != NULL) {
        sqlite3_close((sqlite3 *)ledger->db);
        ledger->db = NULL;
    }

    ledger->opened = 0;
}

KhzSheetStatus khz_ledger_append_cell(KhzLedger *ledger, const KhzCell *cell)
{
    unsigned char expected[KHZ_SHA256_DIGEST_BYTES];
    sqlite3_stmt *stmt;
    KhzSheetStatus status;
    sqlite3_int64 addr;
    int rc;

    status = khz_ledger_ready(ledger);
    if (status != KHZ_SHEET_OK) {
        return status;
    }
    if (cell == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    if (cell->revision == (uint64_t)0) {
        /* Never committed, so there is no proof to record. */
        return KHZ_SHEET_ERR_STATE;
    }

    /* The ledger derives the link itself and compares. It records only what it
       can reproduce, so a caller cannot insert a hash of its own choosing. */
    status = khz_cell_digest(cell, ledger->head, expected);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    if (khz_hash_equal_ct(expected, cell->proof, (size_t)KHZ_SHA256_DIGEST_BYTES) == 0) {
        return KHZ_SHEET_ERR_FORMAT;
    }

    stmt = (sqlite3_stmt *)ledger->append_stmt;
    sqlite3_reset(stmt);
    sqlite3_clear_bindings(stmt);

    addr = (sqlite3_int64)khz_cell_key(cell->col, cell->row);

    rc = sqlite3_bind_int64(stmt, 1, addr);
    if (rc == SQLITE_OK) {
        rc = sqlite3_bind_int64(stmt, 2, (sqlite3_int64)cell->revision);
    }
    if (rc == SQLITE_OK) {
        rc = sqlite3_bind_blob(stmt, 3, cell->proof,
                               (int)KHZ_SHA256_DIGEST_BYTES, SQLITE_TRANSIENT);
    }
    if (rc == SQLITE_OK) {
        rc = sqlite3_bind_blob(stmt, 4, ledger->head,
                               (int)KHZ_SHA256_DIGEST_BYTES, SQLITE_TRANSIENT);
    }
    if (rc == SQLITE_OK) {
        rc = sqlite3_bind_int64(stmt, 5, (sqlite3_int64)time(NULL));
    }

    if (rc != SQLITE_OK) {
        sqlite3_reset(stmt);
        return khz_ledger_fail(ledger, rc, KHZ_SHEET_ERR_STATE);
    }

    rc = sqlite3_step(stmt);
    sqlite3_reset(stmt);

    if (rc == SQLITE_CONSTRAINT) {
        /* This exact commit is already recorded. Advancing the head would
           double-count it. */
        return khz_ledger_fail(ledger, rc, KHZ_SHEET_ERR_STATE);
    }

    if (rc != SQLITE_DONE) {
        return khz_ledger_fail(ledger, rc, KHZ_SHEET_ERR_STATE);
    }

    memcpy(ledger->head, cell->proof, sizeof ledger->head);
    ledger->appended += (uint64_t)1;
    ledger->rows += (uint64_t)1;
    ledger->last_rc = SQLITE_OK;

    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_ledger_append_sheet(KhzLedger *ledger, const KhzSheet *sheet)
{
    size_t total;
    size_t i;
    KhzSheetStatus status;

    status = khz_ledger_ready(ledger);
    if (status != KHZ_SHEET_OK) {
        return status;
    }
    if (sheet == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }
    if (sheet->initialised == 0) {
        return KHZ_SHEET_ERR_STATE;
    }

    total = khz_grid_count(&sheet->grid);

    for (i = (size_t)0; i < total; ++i) {
        status = khz_ledger_append_cell(ledger, &sheet->grid.cells[i]);
        if (status != KHZ_SHEET_OK) {
            return status;
        }
    }

    if (khz_hash_equal_ct(ledger->head, sheet->proof,
                          (size_t)KHZ_SHA256_DIGEST_BYTES) == 0) {
        return KHZ_SHEET_ERR_FORMAT;
    }

    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_ledger_verify_chain(KhzLedger *ledger, uint64_t *checked,
                                       int64_t *failed_id)
{
    unsigned char link[KHZ_SHA256_DIGEST_BYTES];
    sqlite3_stmt *stmt = NULL;
    uint64_t seen = (uint64_t)0;
    KhzSheetStatus status;
    int rc;

    status = khz_ledger_ready(ledger);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    if (failed_id != NULL) {
        *failed_id = (int64_t)-1;
    }

    rc = sqlite3_prepare_v2((sqlite3 *)ledger->db,
                            "SELECT id, hash, prev_hash FROM commits ORDER BY id ASC;",
                            -1, &stmt, NULL);
    if (rc != SQLITE_OK) {
        return khz_ledger_fail(ledger, rc, KHZ_SHEET_ERR_STATE);
    }

    memset(link, 0, sizeof link);

    for (;;) {
        sqlite3_int64 id;
        const void *hash;
        const void *prev;
        int hash_len;
        int prev_len;

        rc = sqlite3_step(stmt);
        if (rc == SQLITE_DONE) {
            break;
        }
        if (rc != SQLITE_ROW) {
            sqlite3_finalize(stmt);
            return khz_ledger_fail(ledger, rc, KHZ_SHEET_ERR_STATE);
        }

        id = sqlite3_column_int64(stmt, 0);
        hash = sqlite3_column_blob(stmt, 1);
        hash_len = sqlite3_column_bytes(stmt, 1);
        prev = sqlite3_column_blob(stmt, 2);
        prev_len = sqlite3_column_bytes(stmt, 2);

        if (hash == NULL || prev == NULL
            || hash_len != (int)KHZ_SHA256_DIGEST_BYTES
            || prev_len != (int)KHZ_SHA256_DIGEST_BYTES) {
            if (failed_id != NULL) {
                *failed_id = (int64_t)id;
            }
            sqlite3_finalize(stmt);
            if (checked != NULL) {
                *checked = seen;
            }
            return KHZ_SHEET_ERR_FORMAT;
        }

        if (khz_hash_equal_ct(prev, link, (size_t)KHZ_SHA256_DIGEST_BYTES) == 0) {
            /* Either a row was altered, removed, or reordered. Which one it was
               cannot be told apart from the hashes alone, so this reports the
               break rather than diagnosing a cause it cannot know. */
            if (failed_id != NULL) {
                *failed_id = (int64_t)id;
            }
            sqlite3_finalize(stmt);
            if (checked != NULL) {
                *checked = seen;
            }
            return KHZ_SHEET_ERR_FORMAT;
        }

        memcpy(link, hash, (size_t)KHZ_SHA256_DIGEST_BYTES);
        ++seen;
    }

    sqlite3_finalize(stmt);

    if (checked != NULL) {
        *checked = seen;
    }

    if (khz_hash_equal_ct(link, ledger->head,
                          (size_t)KHZ_SHA256_DIGEST_BYTES) == 0) {
        return KHZ_SHEET_ERR_FORMAT;
    }

    ledger->last_rc = SQLITE_OK;
    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_ledger_load_chain(KhzLedger *ledger,
                                     unsigned char (**hashes)[KHZ_SHA256_DIGEST_BYTES],
                                     size_t *count)
{
    unsigned char (*buffer)[KHZ_SHA256_DIGEST_BYTES];
    sqlite3_stmt *stmt = NULL;
    size_t capacity;
    size_t filled = (size_t)0;
    KhzSheetStatus status;
    int rc;

    status = khz_ledger_ready(ledger);
    if (status != KHZ_SHEET_OK) {
        return status;
    }
    if (hashes == NULL || count == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    capacity = (size_t)ledger->rows;
    if (capacity == (size_t)0) {
        *hashes = NULL;
        *count = (size_t)0;
        return KHZ_SHEET_OK;
    }

    buffer = (unsigned char (*)[KHZ_SHA256_DIGEST_BYTES])khz_arena_alloc(
        ledger->arena, capacity * (size_t)KHZ_SHA256_DIGEST_BYTES);
    if (buffer == NULL) {
        return KHZ_SHEET_ERR_MEMORY;
    }

    rc = sqlite3_prepare_v2((sqlite3 *)ledger->db,
                            "SELECT hash FROM commits ORDER BY id ASC;",
                            -1, &stmt, NULL);
    if (rc != SQLITE_OK) {
        return khz_ledger_fail(ledger, rc, KHZ_SHEET_ERR_STATE);
    }

    while (filled < capacity) {
        const void *hash;
        int len;

        rc = sqlite3_step(stmt);
        if (rc == SQLITE_DONE) {
            break;
        }
        if (rc != SQLITE_ROW) {
            sqlite3_finalize(stmt);
            return khz_ledger_fail(ledger, rc, KHZ_SHEET_ERR_STATE);
        }

        hash = sqlite3_column_blob(stmt, 0);
        len = sqlite3_column_bytes(stmt, 0);

        if (hash == NULL || len != (int)KHZ_SHA256_DIGEST_BYTES) {
            sqlite3_finalize(stmt);
            return KHZ_SHEET_ERR_FORMAT;
        }

        memcpy(buffer[filled], hash, (size_t)KHZ_SHA256_DIGEST_BYTES);
        ++filled;
    }

    sqlite3_finalize(stmt);

    *hashes = buffer;
    *count = filled;
    ledger->last_rc = SQLITE_OK;

    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_ledger_cell_history_count(KhzLedger *ledger, uint32_t col,
                                             uint32_t row, uint64_t *count)
{
    sqlite3_stmt *stmt = NULL;
    KhzSheetStatus status;
    int rc;

    status = khz_ledger_ready(ledger);
    if (status != KHZ_SHEET_OK) {
        return status;
    }
    if (count == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    rc = sqlite3_prepare_v2((sqlite3 *)ledger->db,
                            "SELECT COUNT(*) FROM commits WHERE cell_addr = ?1;",
                            -1, &stmt, NULL);
    if (rc != SQLITE_OK) {
        return khz_ledger_fail(ledger, rc, KHZ_SHEET_ERR_STATE);
    }

    rc = sqlite3_bind_int64(stmt, 1, (sqlite3_int64)khz_cell_key(col, row));
    if (rc != SQLITE_OK) {
        sqlite3_finalize(stmt);
        return khz_ledger_fail(ledger, rc, KHZ_SHEET_ERR_STATE);
    }

    rc = sqlite3_step(stmt);
    if (rc == SQLITE_ROW) {
        sqlite3_int64 value = sqlite3_column_int64(stmt, 0);

        *count = value > 0 ? (uint64_t)value : (uint64_t)0;
        sqlite3_finalize(stmt);
        ledger->last_rc = SQLITE_OK;
        return KHZ_SHEET_OK;
    }

    sqlite3_finalize(stmt);
    return khz_ledger_fail(ledger, rc, KHZ_SHEET_ERR_STATE);
}

KhzSheetStatus khz_ledger_head(const KhzLedger *ledger,
                               unsigned char out[KHZ_SHA256_DIGEST_BYTES])
{
    if (ledger == NULL || out == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    memcpy(out, ledger->head, (size_t)KHZ_SHA256_DIGEST_BYTES);
    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_ledger_head_hex(const KhzLedger *ledger,
                                   char out[KHZ_SHA256_HEX_BYTES])
{
    if (ledger == NULL || out == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    return khz_sha256_hex(ledger->head, out) == KHZ_HASH_OK ? KHZ_SHEET_OK
                                                            : KHZ_SHEET_ERR_STATE;
}

uint64_t khz_ledger_rows(const KhzLedger *ledger)
{
    return ledger == NULL ? (uint64_t)0 : ledger->rows;
}

uint64_t khz_ledger_appended(const KhzLedger *ledger)
{
    return ledger == NULL ? (uint64_t)0 : ledger->appended;
}

const char *khz_ledger_last_error(const KhzLedger *ledger)
{
    if (ledger == NULL) {
        return "null ledger";
    }
    if (ledger->db == NULL) {
        return ledger->last_rc == SQLITE_OK ? "" : sqlite3_errstr(ledger->last_rc);
    }

    return sqlite3_errmsg((sqlite3 *)ledger->db);
}

#endif /* KHZ_WITH_SQLITE */
