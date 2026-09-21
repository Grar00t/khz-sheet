#include <stdio.h>
#include <string.h>

#include "khz_ledger.h"
#include "khz_sheet.h"

#ifdef KHZ_WITH_SQLITE
#include <sqlite3.h>
#endif

static int fail(const char *what, KhzSheetStatus got, KhzSheetStatus want)
{
    if (got == want) {
        return 0;
    }
    fprintf(stderr, "FAIL %s: got %s want %s\n",
            what, khz_sheet_status_name(got), khz_sheet_status_name(want));
    return 1;
}

int main(void)
{
#ifndef KHZ_WITH_SQLITE
    puts("SKIP ledger unavailable");
    return 0;
#else
    const char *path = "khz-ledger-integrity-test.sqlite";
    KhzSheet sheet;
    KhzLedger ledger;
    KhzArena ledger_arena;
    KhzSheetStatus st;
    const KhzCell *cell = NULL;
    sqlite3 *db = NULL;
    int failures = 0;

    (void)remove(path);
    (void)remove("khz-ledger-integrity-test.sqlite-wal");
    (void)remove("khz-ledger-integrity-test.sqlite-shm");

    if (khz_sheet_init(&sheet, (size_t)1 << 20, (size_t)16) != KHZ_SHEET_OK) {
        return 2;
    }
    if (khz_arena_init(&ledger_arena, (size_t)1 << 20) != KHZ_ARENA_OK) {
        khz_sheet_destroy(&sheet);
        return 2;
    }
    if (khz_ledger_init(&ledger, &ledger_arena) != KHZ_SHEET_OK
        || khz_ledger_open(&ledger, path) != KHZ_SHEET_OK) {
        khz_arena_destroy(&ledger_arena);
        khz_sheet_destroy(&sheet);
        return 2;
    }

    st = khz_sheet_set_i64(&sheet, 0u, 0u, 1);
    failures += fail("set A1", st, KHZ_SHEET_OK);
    st = khz_sheet_get(&sheet, 0u, 0u, &cell);
    failures += fail("get A1", st, KHZ_SHEET_OK);
    if (cell != NULL) {
        failures += fail("append A1", khz_ledger_append_cell(&ledger, cell), KHZ_SHEET_OK);
    }

    st = khz_sheet_set_i64(&sheet, 0u, 1u, 2);
    failures += fail("set A2", st, KHZ_SHEET_OK);
    st = khz_sheet_get(&sheet, 0u, 1u, &cell);
    failures += fail("get A2", st, KHZ_SHEET_OK);
    if (cell != NULL) {
        failures += fail("append A2", khz_ledger_append_cell(&ledger, cell), KHZ_SHEET_OK);
    }

    khz_ledger_close(&ledger);

    if (sqlite3_open_v2(path, &db, SQLITE_OPEN_READWRITE, NULL) != SQLITE_OK) {
        failures++;
    } else {
        if (sqlite3_exec(db,
                         "UPDATE commits SET prev_hash=zeroblob(32) WHERE id=2;",
                         NULL, NULL, NULL) != SQLITE_OK) {
            failures++;
        }
        sqlite3_close(db);
        db = NULL;
    }

    memset(&ledger, 0, sizeof ledger);
    if (khz_ledger_init(&ledger, &ledger_arena) != KHZ_SHEET_OK) {
        failures++;
    } else {
        st = khz_ledger_open(&ledger, path);
        failures += fail("reopen corrupted ledger", st, KHZ_SHEET_ERR_FORMAT);
        if (st == KHZ_SHEET_OK) {
            khz_ledger_close(&ledger);
        }
    }

    khz_arena_destroy(&ledger_arena);
    khz_sheet_destroy(&sheet);
    (void)remove(path);
    (void)remove("khz-ledger-integrity-test.sqlite-wal");
    (void)remove("khz-ledger-integrity-test.sqlite-shm");

    printf("%s failures=%d\n", failures == 0 ? "ALL PASS" : "FAIL", failures);
    return failures == 0 ? 0 : 1;
#endif
}
