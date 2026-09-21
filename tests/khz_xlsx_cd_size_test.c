#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "khz_arena.h"
#include "khz_xlsx_reader.h"

static uint32_t le32(const unsigned char *p)
{
    return (uint32_t)p[0]
         | ((uint32_t)p[1] << 8)
         | ((uint32_t)p[2] << 16)
         | ((uint32_t)p[3] << 24);
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

int main(int argc, char **argv)
{
    unsigned char *bytes;
    size_t size = 0;
    size_t eocd = 0;
    KhzArena arena;
    KhzXlsxReader reader;
    KhzSheetStatus status;

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

    if (khz_arena_init(&arena, (size_t)8 << 20) != KHZ_ARENA_OK) {
        free(bytes);
        return 2;
    }
    status = khz_xlsx_reader_init(&reader, &arena);
    if (status == KHZ_SHEET_OK) status = khz_xlsx_reader_load(&reader, bytes, size);

    khz_arena_destroy(&arena);
    free(bytes);

    if (status != KHZ_SHEET_ERR_FORMAT) {
        fprintf(stderr, "FAIL mutated cd_size: got %s want ERR_FORMAT\n",
                khz_sheet_status_name(status));
        return 1;
    }

    printf("ALL PASS cd_size rejected as ERR_FORMAT\n");
    return 0;
}
