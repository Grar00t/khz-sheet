#include <stdio.h>
#include <string.h>

#include "khz_arena.h"
#include "khz_sheet.h"
#include "khz_xlsx.h"

static int failures;

static uint16_t le16(const unsigned char *p)
{
    return (uint16_t)((uint16_t)p[0] | ((uint16_t)p[1] << 8));
}

static uint32_t le32(const unsigned char *p)
{
    return (uint32_t)p[0]
         | ((uint32_t)p[1] << 8)
         | ((uint32_t)p[2] << 16)
         | ((uint32_t)p[3] << 24);
}

static void fail(const char *what)
{
    fprintf(stderr, "FAIL %s\n", what);
    ++failures;
}

static int validate_zip_directory(const unsigned char *bytes, size_t length)
{
    size_t eocd;
    size_t dir_offset;
    size_t dir_size;
    size_t cursor;
    uint16_t entries;
    uint16_t i;

    if (bytes == NULL || length < (size_t)22) return 0;
    eocd = length - (size_t)22;
    if (le32(bytes + eocd) != 0x06054b50u) return 0;

    entries = le16(bytes + eocd + 10);
    dir_size = (size_t)le32(bytes + eocd + 12);
    dir_offset = (size_t)le32(bytes + eocd + 16);

    /* Independent ZIP invariant: the central directory ends exactly where the
       EOCD begins when the archive has no digital-signature record. */
    if (dir_offset > eocd || dir_size != eocd - dir_offset) return 0;

    cursor = dir_offset;
    for (i = 0; i < entries; ++i) {
        size_t name_len;
        size_t extra_len;
        size_t comment_len;

        if (cursor > eocd || eocd - cursor < (size_t)46) return 0;
        if (le32(bytes + cursor) != 0x02014b50u) return 0;

        name_len = (size_t)le16(bytes + cursor + 28);
        extra_len = (size_t)le16(bytes + cursor + 30);
        comment_len = (size_t)le16(bytes + cursor + 32);
        if (name_len > eocd - cursor - (size_t)46) return 0;
        cursor += (size_t)46 + name_len;
        if (extra_len > eocd - cursor) return 0;
        cursor += extra_len;
        if (comment_len > eocd - cursor) return 0;
        cursor += comment_len;
    }

    return cursor == eocd;
}

int main(void)
{
    KhzSheet sheet;
    KhzArena scratch_a;
    KhzArena scratch_b;
    KhzRational third;
    const unsigned char *a = NULL;
    const unsigned char *b = NULL;
    size_t a_len = 0;
    size_t b_len = 0;
    KhzXlsxReport ra;
    KhzXlsxReport rb;

    if (khz_sheet_init(&sheet, (size_t)1 << 20, (size_t)32) != KHZ_SHEET_OK) return 2;
    if (khz_arena_init(&scratch_a, (size_t)8 << 20) != KHZ_ARENA_OK) {
        khz_sheet_destroy(&sheet);
        return 2;
    }
    if (khz_arena_init(&scratch_b, (size_t)8 << 20) != KHZ_ARENA_OK) {
        khz_arena_destroy(&scratch_a);
        khz_sheet_destroy(&sheet);
        return 2;
    }

    if (khz_sheet_set_i64(&sheet, 0u, 0u, 42) != KHZ_SHEET_OK) fail("set integer");
    if (khz_rational_make(1, 3, &third) != KHZ_SHEET_OK
        || khz_sheet_set_rational(&sheet, 1u, 0u, third) != KHZ_SHEET_OK) {
        fail("set rational");
    }
    if (khz_sheet_set_text(&sheet, 0u, 1u, "alpha & <beta>", (size_t)14) != KHZ_SHEET_OK) {
        fail("set text");
    }
    if (khz_sheet_set_bool(&sheet, 1u, 1u, 1) != KHZ_SHEET_OK) fail("set bool");

    if (khz_xlsx_build(&sheet, &scratch_a, "Audit", &a, &a_len, &ra) != KHZ_SHEET_OK) {
        fail("first build");
    }
    if (khz_xlsx_build(&sheet, &scratch_b, "Audit", &b, &b_len, &rb) != KHZ_SHEET_OK) {
        fail("second build");
    }

    if (a == NULL || !validate_zip_directory(a, a_len)) fail("zip central directory");
    if (a_len != b_len || a == NULL || b == NULL || memcmp(a, b, a_len) != 0) {
        fail("byte deterministic build");
    }
    if (ra.lossy_cells != 1u || rb.lossy_cells != 1u) fail("lossy report");

    khz_arena_destroy(&scratch_b);
    khz_arena_destroy(&scratch_a);
    khz_sheet_destroy(&sheet);

    printf("%s failures=%d\n", failures == 0 ? "ALL PASS" : "FAIL", failures);
    return failures == 0 ? 0 : 1;
}
