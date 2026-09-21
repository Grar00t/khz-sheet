#include <stdio.h>

#include "khz_sheet.h"

static int failures;

static void expect_status(const char *what, KhzSheetStatus got, KhzSheetStatus want)
{
    if (got != want) {
        fprintf(stderr, "FAIL %s: got %s want %s\n",
                what, khz_sheet_status_name(got), khz_sheet_status_name(want));
        ++failures;
    }
}

int main(void)
{
    KhzSheet sheet;
    size_t failed = (size_t)0;

    expect_status("init", khz_sheet_init(&sheet, (size_t)1 << 20, (size_t)8), KHZ_SHEET_OK);
    expect_status("A1=1", khz_sheet_set_i64(&sheet, 0u, 0u, 1), KHZ_SHEET_OK);
    expect_status("A1=2", khz_sheet_set_i64(&sheet, 0u, 0u, 2), KHZ_SHEET_OK);
    expect_status("A1=3", khz_sheet_set_i64(&sheet, 0u, 0u, 3), KHZ_SHEET_OK);

    expect_status("clean chain", khz_sheet_verify_chain(&sheet, &failed), KHZ_SHEET_OK);

    if (sheet.log.entries == NULL || sheet.log.recorded < (uint64_t)3) {
        fprintf(stderr, "FAIL setup did not retain three commits\n");
        ++failures;
    } else {
        sheet.log.entries[0].head[0] ^= 0x01u;
        expect_status("superseded retained head tamper",
                      khz_sheet_verify_chain(&sheet, &failed), KHZ_SHEET_ERR_FORMAT);
    }

    khz_sheet_destroy(&sheet);
    printf("%s failures=%d\n", failures == 0 ? "ALL PASS" : "FAIL", failures);
    return failures == 0 ? 0 : 1;
}
