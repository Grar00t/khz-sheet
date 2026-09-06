#include "khz_xlsx.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "khz_grid.h"
#include "khz_hash.h"
#include "khz_rational.h"

/* ------------------------------------------------------------------ *
 * Fixed-capacity arena buffer.
 *
 * The arena has no realloc by design, so every buffer is sized up front
 * from the sheet's own contents and never grows. Running out sets the
 * overflow flag, which becomes KHZ_SHEET_ERR_MEMORY at the end of the
 * build. A truncated part is never written: an xlsx that opens but is
 * missing rows is worse than a refused write.
 * ------------------------------------------------------------------ */

typedef struct KhzBuf {
    unsigned char *data;
    size_t         cap;
    size_t         len;
    int            overflow;
} KhzBuf;

static KhzSheetStatus khz_buf_init(KhzBuf *buf, KhzArena *arena, size_t cap)
{
    if (cap == (size_t)0) {
        cap = (size_t)1;
    }

    buf->data = (unsigned char *)khz_arena_alloc(arena, cap);
    if (buf->data == NULL) {
        return KHZ_SHEET_ERR_MEMORY;
    }

    buf->cap = cap;
    buf->len = (size_t)0;
    buf->overflow = 0;

    return KHZ_SHEET_OK;
}

static void khz_buf_put(KhzBuf *buf, const void *src, size_t n)
{
    if (buf->overflow != 0) {
        return;
    }
    if (n > buf->cap - buf->len) {
        buf->overflow = 1;
        return;
    }

    memcpy(buf->data + buf->len, src, n);
    buf->len += n;
}

static void khz_buf_putc(KhzBuf *buf, char c)
{
    khz_buf_put(buf, &c, (size_t)1);
}

static void khz_buf_puts(KhzBuf *buf, const char *s)
{
    khz_buf_put(buf, s, strlen(s));
}

static void khz_buf_put_u64(KhzBuf *buf, uint64_t v)
{
    char tmp[24];
    int i = 0;

    if (v == (uint64_t)0) {
        khz_buf_putc(buf, '0');
        return;
    }

    while (v > (uint64_t)0) {
        tmp[i++] = (char)('0' + (int)(v % (uint64_t)10));
        v /= (uint64_t)10;
    }

    while (i > 0) {
        khz_buf_putc(buf, tmp[--i]);
    }
}

static void khz_buf_put_i64(KhzBuf *buf, int64_t v)
{
    if (v < (int64_t)0) {
        khz_buf_putc(buf, '-');
        /* Negated in unsigned space: -INT64_MIN has no int64 representation,
           and wrapping it would print a positive number. */
        khz_buf_put_u64(buf, (uint64_t)0 - (uint64_t)v);
        return;
    }

    khz_buf_put_u64(buf, (uint64_t)v);
}

static void khz_buf_put_le16(KhzBuf *buf, uint16_t v)
{
    unsigned char t[2];

    t[0] = (unsigned char)(v & 0xffu);
    t[1] = (unsigned char)((v >> 8) & 0xffu);
    khz_buf_put(buf, t, (size_t)2);
}

static void khz_buf_put_le32(KhzBuf *buf, uint32_t v)
{
    unsigned char t[4];

    t[0] = (unsigned char)(v & 0xffu);
    t[1] = (unsigned char)((v >> 8) & 0xffu);
    t[2] = (unsigned char)((v >> 16) & 0xffu);
    t[3] = (unsigned char)((v >> 24) & 0xffu);
    khz_buf_put(buf, t, (size_t)4);
}

/* XML 1.0 has no representation at all for most C0 controls - not even a
   numeric character reference. They are replaced with U+FFFD rather than
   dropped, so the text length changes visibly instead of the content changing
   invisibly. */
static void khz_buf_put_xml(KhzBuf *buf, const char *s, size_t n)
{
    size_t i;

    for (i = (size_t)0; i < n; ++i) {
        char c = s[i];

        switch (c) {
        case '&':
            khz_buf_puts(buf, "&amp;");
            break;
        case '<':
            khz_buf_puts(buf, "&lt;");
            break;
        case '>':
            khz_buf_puts(buf, "&gt;");
            break;
        case '"':
            khz_buf_puts(buf, "&quot;");
            break;
        case '\'':
            khz_buf_puts(buf, "&apos;");
            break;
        default:
            if ((unsigned char)c < 0x20u && c != '\t' && c != '\n' && c != '\r') {
                khz_buf_puts(buf, "\xef\xbf\xbd");
            } else {
                khz_buf_putc(buf, c);
            }
            break;
        }
    }
}

/* Bitwise CRC-32, no lookup table. A table would be 1 KiB of static data for
   a routine that runs once per part; the loop is not the bottleneck next to
   file I/O. */
static uint32_t khz_crc32(const unsigned char *data, size_t n)
{
    uint32_t crc = 0xffffffffu;
    size_t i;
    int k;

    for (i = (size_t)0; i < n; ++i) {
        crc ^= (uint32_t)data[i];

        for (k = 0; k < 8; ++k) {
            uint32_t mask = (uint32_t)0 - (crc & 1u);

            crc = (crc >> 1) ^ (0xedb88320u & mask);
        }
    }

    return crc ^ 0xffffffffu;
}

/* ------------------------------------------------------------------ *
 * Shared string table
 * ------------------------------------------------------------------ */

typedef struct KhzSst {
    const char **texts;
    uint32_t    *lens;
    size_t       count;
    size_t       capacity;
    size_t      *slots;      /* index + 1; 0 means empty */
    size_t       slot_count; /* power of two */
    size_t       slot_mask;
    uint64_t     text_bytes;
} KhzSst;

static size_t khz_next_pow2_size(size_t v)
{
    size_t n = (size_t)64;

    while (n < v) {
        size_t next = n << 1;

        if (next <= n) {
            return n;
        }
        n = next;
    }

    return n;
}

static KhzSheetStatus khz_sst_init(KhzSst *sst, KhzArena *arena, size_t capacity)
{
    memset(sst, 0, sizeof *sst);

    if (capacity == (size_t)0) {
        capacity = (size_t)1;
    }

    sst->capacity = capacity;
    sst->slot_count = khz_next_pow2_size(capacity * (size_t)2);
    sst->slot_mask = sst->slot_count - (size_t)1;

    sst->texts = (const char **)khz_arena_alloc(arena, capacity * sizeof(const char *));
    sst->lens = (uint32_t *)khz_arena_alloc(arena, capacity * sizeof(uint32_t));
    sst->slots = (size_t *)khz_arena_alloc_zeroed(arena, sst->slot_count * sizeof(size_t));

    if (sst->texts == NULL || sst->lens == NULL || sst->slots == NULL) {
        return KHZ_SHEET_ERR_MEMORY;
    }

    return KHZ_SHEET_OK;
}

/* Interns one string and yields its index. Deduplication is what makes the
   shared string table worth having, and it is done on bytes, not on a
   normalised form: two strings that differ only in encoding are different
   strings and stay different. */
static KhzSheetStatus khz_sst_intern(KhzSst *sst, const char *text, uint32_t len,
                                     uint32_t *index)
{
    uint64_t hash = khz_fnv1a64(text, (size_t)len);
    size_t slot = (size_t)hash & sst->slot_mask;
    size_t probes = (size_t)0;

    while (probes <= sst->slot_count) {
        size_t stored = sst->slots[slot];

        if (stored == (size_t)0) {
            if (sst->count >= sst->capacity) {
                return KHZ_SHEET_ERR_LIMIT;
            }

            sst->texts[sst->count] = text;
            sst->lens[sst->count] = len;
            sst->slots[slot] = sst->count + (size_t)1;
            sst->text_bytes += (uint64_t)len;
            *index = (uint32_t)sst->count;
            sst->count += (size_t)1;

            return KHZ_SHEET_OK;
        }

        {
            size_t existing = stored - (size_t)1;

            if (sst->lens[existing] == len
                && memcmp(sst->texts[existing], text, (size_t)len) == 0) {
                *index = (uint32_t)existing;
                return KHZ_SHEET_OK;
            }
        }

        slot = (slot + (size_t)1) & sst->slot_mask;
        ++probes;
    }

    return KHZ_SHEET_ERR_LIMIT;
}

/* ------------------------------------------------------------------ *
 * Row-major ordering
 * ------------------------------------------------------------------ */

typedef struct KhzRowKey {
    uint64_t key;   /* row in the high 32 bits, column in the low 32 */
    size_t   index;
} KhzRowKey;

static int khz_rowkey_cmp(const void *a, const void *b)
{
    const KhzRowKey *ka = (const KhzRowKey *)a;
    const KhzRowKey *kb = (const KhzRowKey *)b;

    if (ka->key < kb->key) {
        return -1;
    }
    if (ka->key > kb->key) {
        return 1;
    }

    return 0;
}

KhzSheetStatus khz_xlsx_column_name(uint32_t col, char *out, size_t capacity,
                                    size_t *written)
{
    char tmp[4];
    size_t n = (size_t)0;
    uint32_t v;

    if (out == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }
    if (col >= KHZ_GRID_MAX_COLUMNS) {
        return KHZ_SHEET_ERR_RANGE;
    }
    if (capacity < (size_t)4) {
        return KHZ_SHEET_ERR_RANGE;
    }

    v = col + 1u; /* bijective base 26: A is 1, not 0 */

    while (v > 0u) {
        uint32_t rem = (v - 1u) % 26u;

        tmp[n++] = (char)('A' + (int)rem);
        v = (v - 1u) / 26u;
    }

    {
        size_t i;

        for (i = (size_t)0; i < n; ++i) {
            out[i] = tmp[n - i - (size_t)1];
        }
    }

    out[n] = '\0';

    if (written != NULL) {
        *written = n;
    }

    return KHZ_SHEET_OK;
}

static KhzSheetStatus khz_xlsx_check_name(const char *name, size_t *len_out)
{
    size_t len;
    size_t i;

    if (name == NULL) {
        *len_out = (size_t)0;
        return KHZ_SHEET_OK;
    }

    len = strlen(name);

    if (len == (size_t)0 || len > KHZ_XLSX_MAX_SHEET_NAME) {
        return KHZ_SHEET_ERR_FORMAT;
    }

    for (i = (size_t)0; i < len; ++i) {
        char c = name[i];

        if (c == ':' || c == '\\' || c == '/' || c == '?' || c == '*'
            || c == '[' || c == ']') {
            return KHZ_SHEET_ERR_FORMAT;
        }
    }

    *len_out = len;
    return KHZ_SHEET_OK;
}

static const char *khz_xlsx_error_literal(uint32_t error)
{
    switch ((KhzCellError)error) {
    case KHZ_CELL_ERROR_NULL:
        return "#NULL!";
    case KHZ_CELL_ERROR_DIV0:
        return "#DIV/0!";
    case KHZ_CELL_ERROR_VALUE:
        return "#VALUE!";
    case KHZ_CELL_ERROR_REF:
        return "#REF!";
    case KHZ_CELL_ERROR_NAME:
        return "#NAME?";
    case KHZ_CELL_ERROR_NUM:
        return "#NUM!";
    case KHZ_CELL_ERROR_NA:
        return "#N/A";
    case KHZ_CELL_ERROR_NONE:
    default:
        return "#VALUE!";
    }
}

/* ------------------------------------------------------------------ *
 * Zip container. Stored entries, fixed 1980 timestamps.
 * ------------------------------------------------------------------ */

typedef struct KhzZipEntry {
    const char          *name;
    const unsigned char *data;
    size_t               len;
    uint32_t             crc;
    size_t               local_offset;
} KhzZipEntry;

#define KHZ_ZIP_DOS_TIME ((uint16_t)0)
#define KHZ_ZIP_DOS_DATE ((uint16_t)0x0021) /* 1980-01-01 */
#define KHZ_ZIP_MAX_ENTRIES ((size_t)8)

static void khz_zip_put_local(KhzBuf *pkg, KhzZipEntry *entry)
{
    size_t name_len = strlen(entry->name);

    entry->local_offset = pkg->len;
    entry->crc = khz_crc32(entry->data, entry->len);

    khz_buf_put_le32(pkg, 0x04034b50u);
    khz_buf_put_le16(pkg, (uint16_t)20);
    khz_buf_put_le16(pkg, (uint16_t)0);
    khz_buf_put_le16(pkg, (uint16_t)0); /* stored */
    khz_buf_put_le16(pkg, KHZ_ZIP_DOS_TIME);
    khz_buf_put_le16(pkg, KHZ_ZIP_DOS_DATE);
    khz_buf_put_le32(pkg, entry->crc);
    khz_buf_put_le32(pkg, (uint32_t)entry->len);
    khz_buf_put_le32(pkg, (uint32_t)entry->len);
    khz_buf_put_le16(pkg, (uint16_t)name_len);
    khz_buf_put_le16(pkg, (uint16_t)0);
    khz_buf_put(pkg, entry->name, name_len);
    khz_buf_put(pkg, entry->data, entry->len);
}

static void khz_zip_finish(KhzBuf *pkg, KhzZipEntry *entries, size_t count)
{
    size_t dir_offset = pkg->len;
    size_t i;

    for (i = (size_t)0; i < count; ++i) {
        size_t name_len = strlen(entries[i].name);

        khz_buf_put_le32(pkg, 0x02014b50u);
        khz_buf_put_le16(pkg, (uint16_t)20);
        khz_buf_put_le16(pkg, (uint16_t)20);
        khz_buf_put_le16(pkg, (uint16_t)0);
        khz_buf_put_le16(pkg, (uint16_t)0);
        khz_buf_put_le16(pkg, KHZ_ZIP_DOS_TIME);
        khz_buf_put_le16(pkg, KHZ_ZIP_DOS_DATE);
        khz_buf_put_le32(pkg, entries[i].crc);
        khz_buf_put_le32(pkg, (uint32_t)entries[i].len);
        khz_buf_put_le32(pkg, (uint32_t)entries[i].len);
        khz_buf_put_le16(pkg, (uint16_t)name_len);
        khz_buf_put_le16(pkg, (uint16_t)0);
        khz_buf_put_le16(pkg, (uint16_t)0);
        khz_buf_put_le16(pkg, (uint16_t)0);
        khz_buf_put_le16(pkg, (uint16_t)0);
        khz_buf_put_le32(pkg, 0u);
        khz_buf_put_le32(pkg, (uint32_t)entries[i].local_offset);
        khz_buf_put(pkg, entries[i].name, name_len);
    }

    khz_buf_put_le32(pkg, 0x06054b50u);
    khz_buf_put_le16(pkg, (uint16_t)0);
    khz_buf_put_le16(pkg, (uint16_t)0);
    khz_buf_put_le16(pkg, (uint16_t)count);
    khz_buf_put_le16(pkg, (uint16_t)count);
    khz_buf_put_le32(pkg, (uint32_t)(pkg->len - dir_offset));
    khz_buf_put_le32(pkg, (uint32_t)dir_offset);
    khz_buf_put_le16(pkg, (uint16_t)0);
}

/* ------------------------------------------------------------------ *
 * Package build
 * ------------------------------------------------------------------ */

KhzSheetStatus khz_xlsx_build(const KhzSheet *sheet, KhzArena *scratch,
                              const char *sheet_name,
                              const unsigned char **bytes, size_t *length,
                              KhzXlsxReport *report)
{
    KhzXlsxReport local;
    KhzZipEntry entries[KHZ_ZIP_MAX_ENTRIES];
    KhzBuf sheet_xml;
    KhzBuf sst_xml;
    KhzBuf types_xml;
    KhzBuf root_rels;
    KhzBuf book_xml;
    KhzBuf book_rels;
    KhzBuf pkg;
    KhzSst sst;
    KhzRowKey *keys;
    KhzSheetStatus status;
    const char *name;
    size_t name_len;
    size_t total;
    size_t entry_count = (size_t)0;
    size_t i;
    size_t arena_start;
    uint64_t formula_bytes = (uint64_t)0;
    uint64_t text_bytes = (uint64_t)0;
    uint32_t current_row = 0u;
    int row_open = 0;

    if (sheet == NULL || scratch == NULL || bytes == NULL || length == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }
    if (sheet->initialised == 0) {
        return KHZ_SHEET_ERR_STATE;
    }

    memset(&local, 0, sizeof local);
    arena_start = khz_arena_used(scratch);

    status = khz_xlsx_check_name(sheet_name, &name_len);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    name = sheet_name != NULL ? sheet_name : "Sheet1";
    if (sheet_name == NULL) {
        name_len = strlen(name);
    }

    total = khz_grid_count(&sheet->grid);

    /* One pass to size the buffers. Sizing from the real contents is what lets
       the arena hand out one block per part with no growth and no waste. */
    for (i = (size_t)0; i < total; ++i) {
        const KhzCell *cell = &sheet->grid.cells[i];

        if (cell->kind == (uint32_t)KHZ_CELL_TEXT) {
            text_bytes += (uint64_t)cell->text_len;
        } else if (cell->kind == (uint32_t)KHZ_CELL_FORMULA) {
            formula_bytes += (uint64_t)cell->formula_len;
        }
    }

    if (total > (size_t)0) {
        keys = (KhzRowKey *)khz_arena_alloc(scratch, total * sizeof(KhzRowKey));
        if (keys == NULL) {
            return KHZ_SHEET_ERR_MEMORY;
        }

        for (i = (size_t)0; i < total; ++i) {
            const KhzCell *cell = &sheet->grid.cells[i];

            /* Row major, because that is the order a worksheet part requires.
               khz_cell_key packs column first, so it cannot be reused here. */
            keys[i].key = ((uint64_t)cell->row << 32) | (uint64_t)cell->col;
            keys[i].index = i;
        }

        qsort(keys, total, sizeof(KhzRowKey), khz_rowkey_cmp);
    } else {
        keys = NULL;
    }

    status = khz_sst_init(&sst, scratch, total + (size_t)1);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    /* Escaping can expand a byte to six characters (&apos;), so the worst case
       is budgeted rather than hoped against. */
    status = khz_buf_init(&sheet_xml, scratch,
                          (size_t)512 + total * (size_t)160
                          + (size_t)formula_bytes * (size_t)6);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    khz_buf_puts(&sheet_xml,
                 "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n"
                 "<worksheet xmlns=\"http://schemas.openxmlformats.org/"
                 "spreadsheetml/2006/main\"><sheetData>");

    for (i = (size_t)0; i < total; ++i) {
        const KhzCell *cell = &sheet->grid.cells[keys[i].index];
        char col_name[4];

        if (cell->kind == (uint32_t)KHZ_CELL_EMPTY) {
            /* A blank cell is written as nothing at all. That is what makes
               the grid virtual: an empty coordinate costs zero bytes. */
            continue;
        }

        status = khz_xlsx_column_name(cell->col, col_name, sizeof col_name, NULL);
        if (status != KHZ_SHEET_OK) {
            return status;
        }

        if (row_open == 0 || cell->row != current_row) {
            if (row_open != 0) {
                khz_buf_puts(&sheet_xml, "</row>");
            }

            khz_buf_puts(&sheet_xml, "<row r=\"");
            khz_buf_put_u64(&sheet_xml, (uint64_t)cell->row + (uint64_t)1);
            khz_buf_puts(&sheet_xml, "\">");

            current_row = cell->row;
            row_open = 1;
        }

        khz_buf_puts(&sheet_xml, "<c r=\"");
        khz_buf_puts(&sheet_xml, col_name);
        khz_buf_put_u64(&sheet_xml, (uint64_t)cell->row + (uint64_t)1);
        khz_buf_putc(&sheet_xml, '"');

        switch ((KhzCellKind)cell->kind) {
        case KHZ_CELL_RATIONAL:
            khz_buf_putc(&sheet_xml, '>');
            khz_buf_puts(&sheet_xml, "<v>");

            if (cell->value.den == (int64_t)1) {
                khz_buf_put_i64(&sheet_xml, cell->value.num);
            } else {
                /* The one lossy conversion in the codebase, and it is the
                   format's limit, not the arithmetic's: xlsx numbers are
                   doubles. Counted so the caller knows. */
                double approx = 0.0;
                char tmp[40];

                if (khz_rational_to_double(cell->value, &approx) != KHZ_SHEET_OK) {
                    return KHZ_SHEET_ERR_OVERFLOW;
                }

                if (snprintf(tmp, sizeof tmp, "%.17g", approx) <= 0) {
                    return KHZ_SHEET_ERR_FORMAT;
                }

                khz_buf_puts(&sheet_xml, tmp);
                local.lossy_cells += (uint64_t)1;
            }

            khz_buf_puts(&sheet_xml, "</v>");
            break;

        case KHZ_CELL_TEXT: {
            uint32_t index = 0u;

            if (cell->text == NULL || cell->text_len == 0u) {
                khz_buf_puts(&sheet_xml, "/>");
                local.cells_written += (uint64_t)1;
                continue;
            }

            status = khz_sst_intern(&sst, cell->text, cell->text_len, &index);
            if (status != KHZ_SHEET_OK) {
                return status;
            }

            khz_buf_puts(&sheet_xml, " t=\"s\"><v>");
            khz_buf_put_u64(&sheet_xml, (uint64_t)index);
            khz_buf_puts(&sheet_xml, "</v>");
            break;
        }

        case KHZ_CELL_BOOL:
            khz_buf_puts(&sheet_xml, " t=\"b\"><v>");
            khz_buf_putc(&sheet_xml, cell->bool_value != 0u ? '1' : '0');
            khz_buf_puts(&sheet_xml, "</v>");
            break;

        case KHZ_CELL_ERROR:
            khz_buf_puts(&sheet_xml, " t=\"e\"><v>");
            khz_buf_puts(&sheet_xml, khz_xlsx_error_literal(cell->error));
            khz_buf_puts(&sheet_xml, "</v>");
            break;

        case KHZ_CELL_FORMULA:
            /* The expression is written with no cached <v>. There is no
                evaluator yet, so inventing a cached result would put a number
                in the file that nothing computed. Excel recalculates on
                open. */
            khz_buf_puts(&sheet_xml, "><f>");

            if (cell->formula != NULL && cell->formula_len > 0u) {
                khz_buf_put_xml(&sheet_xml, cell->formula, (size_t)cell->formula_len);
            }

            khz_buf_puts(&sheet_xml, "</f>");
            local.formula_cells += (uint64_t)1;
            break;

        case KHZ_CELL_EMPTY:
        default:
            khz_buf_puts(&sheet_xml, "/>");
            continue;
        }

        khz_buf_puts(&sheet_xml, "</c>");
        local.cells_written += (uint64_t)1;
    }

    if (row_open != 0) {
        khz_buf_puts(&sheet_xml, "</row>");
    }

    khz_buf_puts(&sheet_xml, "</sheetData></worksheet>");

    if (sheet_xml.overflow != 0) {
        return KHZ_SHEET_ERR_MEMORY;
    }

    local.shared_strings = (uint64_t)sst.count;

    status = khz_buf_init(&sst_xml, scratch,
                          (size_t)256 + sst.count * (size_t)48
                          + (size_t)text_bytes * (size_t)6);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    if (sst.count > (size_t)0) {
        khz_buf_puts(&sst_xml,
                     "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n"
                     "<sst xmlns=\"http://schemas.openxmlformats.org/"
                     "spreadsheetml/2006/main\" count=\"");
        khz_buf_put_u64(&sst_xml, (uint64_t)sst.count);
        khz_buf_puts(&sst_xml, "\" uniqueCount=\"");
        khz_buf_put_u64(&sst_xml, (uint64_t)sst.count);
        khz_buf_puts(&sst_xml, "\">");

        for (i = (size_t)0; i < sst.count; ++i) {
            /* xml:space preserve, always. Excel trims leading and trailing
               whitespace without it, which silently edits the user's data. */
            khz_buf_puts(&sst_xml, "<si><t xml:space=\"preserve\">");
            khz_buf_put_xml(&sst_xml, sst.texts[i], (size_t)sst.lens[i]);
            khz_buf_puts(&sst_xml, "</t></si>");
        }

        khz_buf_puts(&sst_xml, "</sst>");

        if (sst_xml.overflow != 0) {
            return KHZ_SHEET_ERR_MEMORY;
        }
    }

    status = khz_buf_init(&types_xml, scratch, (size_t)1024);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    khz_buf_puts(&types_xml,
                 "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n"
                 "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/"
                 "content-types\">"
                 "<Default Extension=\"rels\" ContentType=\"application/"
                 "vnd.openxmlformats-package.relationships+xml\"/>"
                 "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                 "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/"
                 "vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>"
                 "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\""
                 "application/vnd.openxmlformats-officedocument.spreadsheetml."
                 "worksheet+xml\"/>");

    if (sst.count > (size_t)0) {
        khz_buf_puts(&types_xml,
                     "<Override PartName=\"/xl/sharedStrings.xml\" ContentType=\""
                     "application/vnd.openxmlformats-officedocument.spreadsheetml."
                     "sharedStrings+xml\"/>");
    }

    khz_buf_puts(&types_xml, "</Types>");

    status = khz_buf_init(&root_rels, scratch, (size_t)512);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    khz_buf_puts(&root_rels,
                 "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n"
                 "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/"
                 "2006/relationships\">"
                 "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/"
                 "officeDocument/2006/relationships/officeDocument\" "
                 "Target=\"xl/workbook.xml\"/></Relationships>");

    status = khz_buf_init(&book_xml, scratch, (size_t)768);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    khz_buf_puts(&book_xml,
                 "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n"
                 "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/"
                 "2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/"
                 "officeDocument/2006/relationships\"><sheets><sheet name=\"");
    khz_buf_put_xml(&book_xml, name, name_len);
    khz_buf_puts(&book_xml, "\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");

    status = khz_buf_init(&book_rels, scratch, (size_t)768);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    khz_buf_puts(&book_rels,
                 "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n"
                 "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/"
                 "2006/relationships\">"
                 "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/"
                 "officeDocument/2006/relationships/worksheet\" "
                 "Target=\"worksheets/sheet1.xml\"/>");

    if (sst.count > (size_t)0) {
        khz_buf_puts(&book_rels,
                     "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats."
                     "org/officeDocument/2006/relationships/sharedStrings\" "
                     "Target=\"sharedStrings.xml\"/>");
    }

    khz_buf_puts(&book_rels, "</Relationships>");

    if (types_xml.overflow != 0 || root_rels.overflow != 0
        || book_xml.overflow != 0 || book_rels.overflow != 0) {
        return KHZ_SHEET_ERR_MEMORY;
    }

    entries[entry_count].name = "[Content_Types].xml";
    entries[entry_count].data = types_xml.data;
    entries[entry_count].len = types_xml.len;
    ++entry_count;

    entries[entry_count].name = "_rels/.rels";
    entries[entry_count].data = root_rels.data;
    entries[entry_count].len = root_rels.len;
    ++entry_count;

    entries[entry_count].name = "xl/workbook.xml";
    entries[entry_count].data = book_xml.data;
    entries[entry_count].len = book_xml.len;
    ++entry_count;

    entries[entry_count].name = "xl/_rels/workbook.xml.rels";
    entries[entry_count].data = book_rels.data;
    entries[entry_count].len = book_rels.len;
    ++entry_count;

    entries[entry_count].name = "xl/worksheets/sheet1.xml";
    entries[entry_count].data = sheet_xml.data;
    entries[entry_count].len = sheet_xml.len;
    ++entry_count;

    if (sst.count > (size_t)0) {
        entries[entry_count].name = "xl/sharedStrings.xml";
        entries[entry_count].data = sst_xml.data;
        entries[entry_count].len = sst_xml.len;
        ++entry_count;
    }

    {
        size_t package_cap = (size_t)512 + (size_t)22;

        for (i = (size_t)0; i < entry_count; ++i) {
            size_t name_bytes = strlen(entries[i].name);

            /* 30 byte local header + 46 byte central record + the name twice. */
            package_cap += entries[i].len + name_bytes * (size_t)2 + (size_t)76;
        }

        status = khz_buf_init(&pkg, scratch, package_cap);
        if (status != KHZ_SHEET_OK) {
            return status;
        }
    }

    for (i = (size_t)0; i < entry_count; ++i) {
        khz_zip_put_local(&pkg, &entries[i]);
    }

    khz_zip_finish(&pkg, entries, entry_count);

    if (pkg.overflow != 0) {
        return KHZ_SHEET_ERR_MEMORY;
    }

    local.bytes_written = (uint64_t)pkg.len;
    local.arena_bytes_used = (uint64_t)(khz_arena_used(scratch) - arena_start);

    *bytes = pkg.data;
    *length = pkg.len;

    if (report != NULL) {
        *report = local;
    }

    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_xlsx_write(const KhzSheet *sheet, KhzArena *scratch,
                              const char *path, const char *sheet_name,
                              KhzXlsxReport *report)
{
    const unsigned char *bytes = NULL;
    size_t length = (size_t)0;
    size_t mark;
    size_t path_len;
    KhzSheetStatus status;
    FILE *file;

    if (sheet == NULL || scratch == NULL || path == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    path_len = strlen(path);
    if (path_len == (size_t)0 || path_len > KHZ_XLSX_MAX_PATH) {
        return KHZ_SHEET_ERR_RANGE;
    }

    /* Mark before, release after: a completed write leaves the arena offset
       exactly where it found it, so writing a file repeatedly does not creep
       towards exhaustion. */
    mark = khz_arena_mark(scratch);

    status = khz_xlsx_build(sheet, scratch, sheet_name, &bytes, &length, report);
    if (status != KHZ_SHEET_OK) {
        khz_arena_release(scratch, mark);
        return status;
    }

    file = fopen(path, "wb");
    if (file == NULL) {
        khz_arena_release(scratch, mark);
        return KHZ_SHEET_ERR_OS;
    }

    if (fwrite(bytes, (size_t)1, length, file) != length) {
        /* Partially written file left on disk deliberately untouched: deleting
           it here would hide the failure, and the status already says the
           write failed. */
        fclose(file);
        khz_arena_release(scratch, mark);
        return KHZ_SHEET_ERR_OS;
    }

    if (fclose(file) != 0) {
        khz_arena_release(scratch, mark);
        return KHZ_SHEET_ERR_OS;
    }

    khz_arena_release(scratch, mark);
    return KHZ_SHEET_OK;
}
