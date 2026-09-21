#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "khz_arena.h"
#include "khz_xlsx_reader.h"

static uint16_t le16(const unsigned char *p)
{
    return (uint16_t)((uint16_t)p[0] | (uint16_t)((uint16_t)p[1] << 8));
}

static uint32_t le32(const unsigned char *p)
{
    return (uint32_t)p[0]
         | ((uint32_t)p[1] << 8)
         | ((uint32_t)p[2] << 16)
         | ((uint32_t)p[3] << 24);
}

static void put_le16(unsigned char *p, uint16_t value)
{
    p[0] = (unsigned char)(value & 0xffu);
    p[1] = (unsigned char)((value >> 8) & 0xffu);
}

static void put_le32(unsigned char *p, uint32_t value)
{
    p[0] = (unsigned char)(value & 0xffu);
    p[1] = (unsigned char)((value >> 8) & 0xffu);
    p[2] = (unsigned char)((value >> 16) & 0xffu);
    p[3] = (unsigned char)((value >> 24) & 0xffu);
}

static unsigned char *read_file(const char *path, size_t *size_out)
{
    FILE *f = fopen(path, "rb");
    unsigned char *bytes;
    long end;
    size_t size;

    if (f == NULL) return NULL;
    if (fseek(f, 0, SEEK_END) != 0) { fclose(f); return NULL; }
    end = ftell(f);
    if (end < 0 || fseek(f, 0, SEEK_SET) != 0) { fclose(f); return NULL; }
    size = (size_t)end;
    bytes = (unsigned char *)malloc(size == 0 ? (size_t)1 : size);
    if (bytes == NULL) { fclose(f); return NULL; }
    if (fread(bytes, 1, size, f) != size) { free(bytes); fclose(f); return NULL; }
    fclose(f);
    *size_out = size;
    return bytes;
}

static int find_eocd(const unsigned char *bytes, size_t size, size_t *offset)
{
    size_t back;
    if (size < (size_t)22) return 0;
    for (back = 0; back <= (size_t)65535 && back <= size - (size_t)22; ++back) {
        size_t at = size - (size_t)22 - back;
        if (le32(bytes + at) == KHZ_XLSX_SIG_EOCD) {
            *offset = at;
            return 1;
        }
    }
    return 0;
}

static KhzSheetStatus load_status(const unsigned char *bytes, size_t size)
{
    KhzArena arena;
    KhzXlsxReader reader;
    KhzSheetStatus status;

    if (khz_arena_init(&arena, (size_t)8 << 20) != KHZ_ARENA_OK) {
        return KHZ_SHEET_ERR_MEMORY;
    }
    status = khz_xlsx_reader_init(&reader, &arena);
    if (status == KHZ_SHEET_OK) status = khz_xlsx_reader_load(&reader, bytes, size);
    khz_arena_destroy(&arena);
    return status;
}

static int expect_status(const char *what, KhzSheetStatus got, KhzSheetStatus want)
{
    if (got == want) return 0;
    fprintf(stderr, "FAIL %s: got %s want %s\n",
            what, khz_sheet_status_name(got), khz_sheet_status_name(want));
    return 1;
}

int main(int argc, char **argv)
{
    unsigned char *bytes;
    size_t size = 0;
    size_t eocd = 0;
    KhzSheetStatus status;
    int failures = 0;

    if (argc != 2) {
        fprintf(stderr, "usage: khz_xlsx_cd_size_test <fixture.xlsx>\n");
        return 2;
    }

    bytes = read_file(argv[1], &size);
    if (bytes == NULL || !find_eocd(bytes, size, &eocd)) {
        free(bytes);
        return 2;
    }

    /* Keep the real central records and entry count, but claim the directory
       itself is only one byte long. A strict reader must reject before walking
       any record outside that declared extent. */
    put_le32(bytes + eocd + 12, 1u);
    status = load_status(bytes, size);
    failures += expect_status("mutated cd_size", status, KHZ_SHEET_ERR_FORMAT);
    free(bytes);

    bytes = read_file(argv[1], &size);
    if (bytes == NULL || !find_eocd(bytes, size, &eocd)) {
        free(bytes);
        return 2;
    }

    /* XLSX is one ZIP file. A nonzero EOCD disk number declares a split or
       multi-disk archive, which this reader cannot resolve from one byte
       buffer. It must be refused rather than silently interpreted as local. */
    if (le16(bytes + eocd + 4) != 0u) {
        free(bytes);
        return 2;
    }
    put_le16(bytes + eocd + 4, 1u);
    status = load_status(bytes, size);
    failures += expect_status("multi-disk EOCD", status, KHZ_SHEET_ERR_UNSUPPORTED);
    free(bytes);

    bytes = read_file(argv[1], &size);
    if (bytes == NULL || !find_eocd(bytes, size, &eocd)) {
        free(bytes);
        return 2;
    }

    /* The central directory and the local header describe the same entry. If
       their names disagree, selecting one side creates parser-confusion
       semantics. Mutate one byte of the first local name while leaving the
       central name and payload untouched; a strict reader must reject it. */
    {
        size_t central = (size_t)le32(bytes + eocd + 16);
        size_t local;
        size_t local_name_len;
        size_t central_name_len;
        if (central > size || size - central < (size_t)46
            || le32(bytes + central) != KHZ_XLSX_SIG_CENTRAL) {
            free(bytes);
            return 2;
        }
        local = (size_t)le32(bytes + central + 42);
        central_name_len = (size_t)le16(bytes + central + 28);
        if (local > size || size - local < (size_t)30
            || le32(bytes + local) != KHZ_XLSX_SIG_LOCAL) {
            free(bytes);
            return 2;
        }
        local_name_len = (size_t)le16(bytes + local + 26);
        if (local_name_len == 0 || local_name_len != central_name_len
            || local_name_len > size - (local + (size_t)30)) {
            free(bytes);
            return 2;
        }
        bytes[local + (size_t)30] ^= (unsigned char)1u;
    }
    status = load_status(bytes, size);
    failures += expect_status("central/local filename mismatch", status, KHZ_SHEET_ERR_FORMAT);
    free(bytes);

    bytes = read_file(argv[1], &size);
    if (bytes == NULL || !find_eocd(bytes, size, &eocd)) {
        free(bytes);
        return 2;
    }

    /* EOCD comment length is the framing contract for the tail of the archive.
       Appending bytes without increasing that field must not be accepted as a
       second interpretation of the same ZIP. */
    {
        unsigned char *extended = (unsigned char *)malloc(size + (size_t)4);
        if (extended == NULL) {
            free(bytes);
            return 2;
        }
        memcpy(extended, bytes, size);
        memcpy(extended + size, "JUNK", (size_t)4);
        status = load_status(extended, size + (size_t)4);
        failures += expect_status("trailing bytes after EOCD", status, KHZ_SHEET_ERR_FORMAT);
        free(extended);
    }
    free(bytes);

    if (failures != 0) {
        fprintf(stderr, "FAILURES=%d\n", failures);
        return 1;
    }

    printf("ALL PASS strict central-directory and EOCD framing checks\n");
    return 0;
}
