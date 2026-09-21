#include "khz_xlsx.h"

#include <locale.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "khz_grid.h"
#include "khz_hash.h"
#include "khz_rational.h"

typedef struct KhzBuf {
    unsigned char *data;
    size_t cap;
    size_t len;
    int overflow;
} KhzBuf;

static int khz_size_add(size_t a, size_t b, size_t *out)
{
    if (out == NULL || a > SIZE_MAX - b) return 0;
    *out = a + b;
    return 1;
}

static int khz_size_mul(size_t a, size_t b, size_t *out)
{
    if (out == NULL) return 0;
    if (a != (size_t)0 && b > SIZE_MAX / a) return 0;
    *out = a * b;
    return 1;
}

static KhzSheetStatus khz_buf_init(KhzBuf *buf, KhzArena *arena, size_t cap)
{
    if (cap == (size_t)0) cap = (size_t)1;
    buf->data = (unsigned char *)khz_arena_alloc(arena, cap);
    if (buf->data == NULL) return KHZ_SHEET_ERR_MEMORY;
    buf->cap = cap;
    buf->len = (size_t)0;
    buf->overflow = 0;
    return KHZ_SHEET_OK;
}

static void khz_buf_put(KhzBuf *buf, const void *src, size_t n)
{
    if (buf->overflow != 0) return;
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
    while (i > 0) khz_buf_putc(buf, tmp[--i]);
}

static void khz_buf_put_i64(KhzBuf *buf, int64_t v)
{
    if (v < (int64_t)0) {
        khz_buf_putc(buf, '-');
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

static void khz_buf_put_replacement(KhzBuf *buf)
{
    static const unsigned char replacement[3] = { 0xefu, 0xbfu, 0xbdu };
    khz_buf_put(buf, replacement, sizeof replacement);
}

/* Returns the number of bytes in one valid UTF-8 scalar, or zero for an
   invalid leading/continuation sequence. Overlong encodings, UTF-16 surrogate
   values and scalars above U+10FFFF are rejected here rather than copied into
   XML that claims to be UTF-8. */
static size_t khz_utf8_scalar(const unsigned char *s, size_t n, uint32_t *scalar)
{
    unsigned char a;
    unsigned char b;
    unsigned char c;
    unsigned char d;

    if (s == NULL || scalar == NULL || n == (size_t)0) return (size_t)0;
    a = s[0];
    if (a < 0x80u) {
        *scalar = (uint32_t)a;
        return (size_t)1;
    }
    if (a >= 0xc2u && a <= 0xdfu) {
        if (n < (size_t)2) return (size_t)0;
        b = s[1];
        if ((b & 0xc0u) != 0x80u) return (size_t)0;
        *scalar = ((uint32_t)(a & 0x1fu) << 6) | (uint32_t)(b & 0x3fu);
        return (size_t)2;
    }
    if (a >= 0xe0u && a <= 0xefu) {
        if (n < (size_t)3) return (size_t)0;
        b = s[1]; c = s[2];
        if ((b & 0xc0u) != 0x80u || (c & 0xc0u) != 0x80u) return (size_t)0;
        if (a == 0xe0u && b < 0xa0u) return (size_t)0;
        if (a == 0xedu && b >= 0xa0u) return (size_t)0;
        *scalar = ((uint32_t)(a & 0x0fu) << 12)
                | ((uint32_t)(b & 0x3fu) << 6)
                | (uint32_t)(c & 0x3fu);
        return (size_t)3;
    }
    if (a >= 0xf0u && a <= 0xf4u) {
        if (n < (size_t)4) return (size_t)0;
        b = s[1]; c = s[2]; d = s[3];
        if ((b & 0xc0u) != 0x80u || (c & 0xc0u) != 0x80u || (d & 0xc0u) != 0x80u) {
            return (size_t)0;
        }
        if (a == 0xf0u && b < 0x90u) return (size_t)0;
        if (a == 0xf4u && b > 0x8fu) return (size_t)0;
        *scalar = ((uint32_t)(a & 0x07u) << 18)
                | ((uint32_t)(b & 0x3fu) << 12)
                | ((uint32_t)(c & 0x3fu) << 6)
                | (uint32_t)(d & 0x3fu);
        return (size_t)4;
    }
    return (size_t)0;
}

static int khz_xml_scalar_allowed(uint32_t cp)
{
    return cp == 0x09u || cp == 0x0au || cp == 0x0du
        || (cp >= 0x20u && cp <= 0xd7ffu)
        || (cp >= 0xe000u && cp <= 0xfffdu)
        || (cp >= 0x10000u && cp <= 0x10ffffu);
}

/* XML escaping and UTF-8 validation are one boundary. Invalid byte sequences
   and XML-forbidden scalars become U+FFFD. A literal CR is emitted as a
   character reference so XML end-of-line normalisation cannot silently turn
   user data into LF on the next parse. */
static void khz_buf_put_xml(KhzBuf *buf, const char *s, size_t n)
{
    size_t i = (size_t)0;

    while (i < n) {
        const unsigned char *u = (const unsigned char *)s + i;
        uint32_t cp = 0u;
        size_t width;

        if (u[0] < 0x80u) {
            char c = (char)u[0];
            switch (c) {
                case '&': khz_buf_puts(buf, "&amp;"); break;
                case '<': khz_buf_puts(buf, "&lt;"); break;
                case '>': khz_buf_puts(buf, "&gt;"); break;
                case '"': khz_buf_puts(buf, "&quot;"); break;
                case '\'': khz_buf_puts(buf, "&apos;"); break;
                case '\r': khz_buf_puts(buf, "&#xD;"); break;
                default:
                    if (u[0] < 0x20u && c != '\t' && c != '\n') {
                        khz_buf_put_replacement(buf);
                    } else {
                        khz_buf_putc(buf, c);
                    }
                    break;
            }
            ++i;
            continue;
        }

        width = khz_utf8_scalar(u, n - i, &cp);
        if (width == (size_t)0) {
            khz_buf_put_replacement(buf);
            ++i;
            continue;
        }
        if (!khz_xml_scalar_allowed(cp)) {
            khz_buf_put_replacement(buf);
            i += width;
            continue;
        }

        khz_buf_put(buf, u, width);
        i += width;
    }
}

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

typedef struct KhzSst {
    const char **texts;
    uint32_t *lens;
    size_t count;
    size_t capacity;
    size_t *slots;
    size_t slot_count;
    size_t slot_mask;
    uint64_t text_bytes;
} KhzSst;

static size_t khz_next_pow2_size(size_t v)
{
    size_t n = (size_t)64;
    while (n < v) {
        size_t next = n << 1;
        if (next <= n) return (size_t)0;
        n = next;
    }
    return n;
}

static KhzSheetStatus khz_sst_init(KhzSst *sst, KhzArena *arena, size_t capacity)
{
    size_t slots_wanted;
    size_t bytes;

    memset(sst, 0, sizeof *sst);
    if (capacity == (size_t)0) capacity = (size_t)1;
    if (!khz_size_mul(capacity, (size_t)2, &slots_wanted)) return KHZ_SHEET_ERR_LIMIT;

    sst->capacity = capacity;
    sst->slot_count = khz_next_pow2_size(slots_wanted);
    if (sst->slot_count == (size_t)0) return KHZ_SHEET_ERR_LIMIT;
    sst->slot_mask = sst->slot_count - (size_t)1;

    if (!khz_size_mul(capacity, sizeof(const char *), &bytes)) return KHZ_SHEET_ERR_LIMIT;
    sst->texts = (const char **)khz_arena_alloc(arena, bytes);
    if (!khz_size_mul(capacity, sizeof(uint32_t), &bytes)) return KHZ_SHEET_ERR_LIMIT;
    sst->lens = (uint32_t *)khz_arena_alloc(arena, bytes);
    if (!khz_size_mul(sst->slot_count, sizeof(size_t), &bytes)) return KHZ_SHEET_ERR_LIMIT;
    sst->slots = (size_t *)khz_arena_alloc_zeroed(arena, bytes);

    if (sst->texts == NULL || sst->lens == NULL || sst->slots == NULL) {
        return KHZ_SHEET_ERR_MEMORY;
    }
    return KHZ_SHEET_OK;
}

static KhzSheetStatus khz_sst_intern(KhzSst *sst, const char *text, uint32_t len,
                                     uint32_t *index)
{
    uint64_t hash = khz_fnv1a64(text, (size_t)len);
    size_t slot = (size_t)hash & sst->slot_mask;
    size_t probes = (size_t)0;

    while (probes <= sst->slot_count) {
        size_t stored = sst->slots[slot];
        if (stored == (size_t)0) {
            if (sst->count >= sst->capacity) return KHZ_SHEET_ERR_LIMIT;
            if (sst->text_bytes > UINT64_MAX - (uint64_t)len) return KHZ_SHEET_ERR_OVERFLOW;
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

typedef struct KhzRowKey {
    uint64_t key;
    size_t index;
} KhzRowKey;

static int khz_rowkey_cmp(const void *a, const void *b)
{
    const KhzRowKey *ka = (const KhzRowKey *)a;
    const KhzRowKey *kb = (const KhzRowKey *)b;
    if (ka->key < kb->key) return -1;
    if (ka->key > kb->key) return 1;
    return 0;
}

KhzSheetStatus khz_xlsx_column_name(uint32_t col, char *out, size_t capacity,
                                    size_t *written)
{
    char tmp[4];
    size_t n = (size_t)0;
    uint32_t v;

    if (out == NULL) return KHZ_SHEET_ERR_NULL;
    if (col >= KHZ_GRID_MAX_COLUMNS) return KHZ_SHEET_ERR_RANGE;
    if (capacity < (size_t)4) return KHZ_SHEET_ERR_RANGE;

    v = col + 1u;
    while (v > 0u) {
        uint32_t rem = (v - 1u) % 26u;
        tmp[n++] = (char)('A' + (int)rem);
        v = (v - 1u) / 26u;
    }
    {
        size_t i;
        for (i = (size_t)0; i < n; ++i) out[i] = tmp[n - i - (size_t)1];
    }
    out[n] = '\0';
    if (written != NULL) *written = n;
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
    if (len == (size_t)0 || len > KHZ_XLSX_MAX_SHEET_NAME) return KHZ_SHEET_ERR_FORMAT;
    for (i = (size_t)0; i < len; ++i) {
        char c = name[i];
        if (c == ':' || c == '\\' || c == '/' || c == '?' || c == '*'
            || c == '[' || c == ']') return KHZ_SHEET_ERR_FORMAT;
    }
    *len_out = len;
    return KHZ_SHEET_OK;
}

static const char *khz_xlsx_error_literal(uint32_t error)
{
    switch ((KhzCellError)error) {
        case KHZ_CELL_ERROR_NULL: return "#NULL!";
        case KHZ_CELL_ERROR_DIV0: return "#DIV/0!";
        case KHZ_CELL_ERROR_VALUE: return "#VALUE!";
        case KHZ_CELL_ERROR_REF: return "#REF!";
        case KHZ_CELL_ERROR_NAME: return "#NAME?";
        case KHZ_CELL_ERROR_NUM: return "#NUM!";
        case KHZ_CELL_ERROR_NA: return "#N/A";
        case KHZ_CELL_ERROR_NONE:
        default: return "#VALUE!";
    }
}

typedef struct KhzZipEntry {
    const char *name;
    const unsigned char *data;
    size_t len;
    uint32_t crc;
    size_t local_offset;
} KhzZipEntry;

#define KHZ_ZIP_DOS_TIME ((uint16_t)0)
#define KHZ_ZIP_DOS_DATE ((uint16_t)0x0021)
#define KHZ_ZIP_MAX_ENTRIES ((size_t)8)

static void khz_zip_put_local(KhzBuf *pkg, KhzZipEntry *entry)
{
    size_t name_len = strlen(entry->name);
    entry->local_offset = pkg->len;
    entry->crc = khz_crc32(entry->data, entry->len);

    khz_buf_put_le32(pkg, 0x04034b50u);
    khz_buf_put_le16(pkg, (uint16_t)20);
    khz_buf_put_le16(pkg, (uint16_t)0);
    khz_buf_put_le16(pkg, (uint16_t)0);
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
    size_t dir_size;
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

    /* The central-directory size is the byte range BEFORE the EOCD record.
       Computing it after writing the EOCD signature and fixed fields adds 12
       bytes and produces an archive strict ZIP readers reject. */
    dir_size = pkg->len - dir_offset;

    khz_buf_put_le32(pkg, 0x06054b50u);
    khz_buf_put_le16(pkg, (uint16_t)0);
    khz_buf_put_le16(pkg, (uint16_t)0);
    khz_buf_put_le16(pkg, (uint16_t)count);
    khz_buf_put_le16(pkg, (uint16_t)count);
    khz_buf_put_le32(pkg, (uint32_t)dir_size);
    khz_buf_put_le32(pkg, (uint32_t)dir_offset);
    khz_buf_put_le16(pkg, (uint16_t)0);
}

static KhzSheetStatus khz_format_double(char *out, size_t capacity, double value)
{
    struct lconv *lc;
    const char *decimal;
    size_t decimal_len;
    char *at;
    char *p;
    int written;

    written = snprintf(out, capacity, "%.17g", value);
    if (written <= 0 || (size_t)written >= capacity) return KHZ_SHEET_ERR_FORMAT;

    /* C printf follows LC_NUMERIC. XLSX does not: its decimal separator is
       always '.'. Normalise the locale separator in-place without changing the
       process locale. */
    lc = localeconv();
    decimal = lc != NULL ? lc->decimal_point : ".";
    if (decimal != NULL && strcmp(decimal, ".") != 0) {
        decimal_len = strlen(decimal);
        if (decimal_len == (size_t)0) return KHZ_SHEET_ERR_FORMAT;
        at = strstr(out, decimal);
        if (at != NULL) {
            if (decimal_len > (size_t)1) {
                memmove(at + 1, at + decimal_len, strlen(at + decimal_len) + (size_t)1);
            }
            *at = '.';
        }
    }

    for (p = out; *p != '\0'; ++p) {
        if ((*p >= '0' && *p <= '9') || *p == '+' || *p == '-' || *p == '.'
            || *p == 'e' || *p == 'E') {
            continue;
        }
        return KHZ_SHEET_ERR_FORMAT;
    }
    return KHZ_SHEET_OK;
}

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
    if (sheet->initialised == 0) return KHZ_SHEET_ERR_STATE;

    memset(&local, 0, sizeof local);
    arena_start = khz_arena_used(scratch);
    status = khz_xlsx_check_name(sheet_name, &name_len);
    if (status != KHZ_SHEET_OK) return status;

    name = sheet_name != NULL ? sheet_name : "Sheet1";
    if (sheet_name == NULL) name_len = strlen(name);
    total = khz_grid_count(&sheet->grid);

    for (i = (size_t)0; i < total; ++i) {
        const KhzCell *cell = &sheet->grid.cells[i];
        if (cell->kind == (uint32_t)KHZ_CELL_TEXT) {
            if (text_bytes > UINT64_MAX - (uint64_t)cell->text_len) return KHZ_SHEET_ERR_OVERFLOW;
            text_bytes += (uint64_t)cell->text_len;
        } else if (cell->kind == (uint32_t)KHZ_CELL_FORMULA) {
            if (formula_bytes > UINT64_MAX - (uint64_t)cell->formula_len) return KHZ_SHEET_ERR_OVERFLOW;
            formula_bytes += (uint64_t)cell->formula_len;
        }
    }

    if (total > (size_t)0) {
        size_t key_bytes;
        if (!khz_size_mul(total, sizeof(KhzRowKey), &key_bytes)) return KHZ_SHEET_ERR_LIMIT;
        keys = (KhzRowKey *)khz_arena_alloc(scratch, key_bytes);
        if (keys == NULL) return KHZ_SHEET_ERR_MEMORY;
        for (i = (size_t)0; i < total; ++i) {
            const KhzCell *cell = &sheet->grid.cells[i];
            keys[i].key = ((uint64_t)cell->row << 32) | (uint64_t)cell->col;
            keys[i].index = i;
        }
        qsort(keys, total, sizeof(KhzRowKey), khz_rowkey_cmp);
    } else {
        keys = NULL;
    }

    if (total == SIZE_MAX) return KHZ_SHEET_ERR_LIMIT;
    status = khz_sst_init(&sst, scratch, total + (size_t)1);
    if (status != KHZ_SHEET_OK) return status;

    {
        size_t cap = (size_t)512;
        size_t term;
        if (!khz_size_mul(total, (size_t)160, &term) || !khz_size_add(cap, term, &cap)) {
            return KHZ_SHEET_ERR_LIMIT;
        }
        if (formula_bytes > (uint64_t)SIZE_MAX
            || !khz_size_mul((size_t)formula_bytes, (size_t)6, &term)
            || !khz_size_add(cap, term, &cap)) {
            return KHZ_SHEET_ERR_LIMIT;
        }
        status = khz_buf_init(&sheet_xml, scratch, cap);
        if (status != KHZ_SHEET_OK) return status;
    }

    khz_buf_puts(&sheet_xml,
                 "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n"
                 "<worksheet xmlns=\"http://schemas.openxmlformats.org/"
                 "spreadsheetml/2006/main\"><sheetData>");

    for (i = (size_t)0; i < total; ++i) {
        const KhzCell *cell = &sheet->grid.cells[keys[i].index];
        char col_name[4];

        if (cell->kind == (uint32_t)KHZ_CELL_EMPTY) continue;
        status = khz_xlsx_column_name(cell->col, col_name, sizeof col_name, NULL);
        if (status != KHZ_SHEET_OK) return status;

        if (row_open == 0 || cell->row != current_row) {
            if (row_open != 0) khz_buf_puts(&sheet_xml, "</row>");
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
                    double approx = 0.0;
                    char tmp[64];
                    if (khz_rational_to_double(cell->value, &approx) != KHZ_SHEET_OK) {
                        return KHZ_SHEET_ERR_OVERFLOW;
                    }
                    status = khz_format_double(tmp, sizeof tmp, approx);
                    if (status != KHZ_SHEET_OK) return status;
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
                if (status != KHZ_SHEET_OK) return status;
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

    if (row_open != 0) khz_buf_puts(&sheet_xml, "</row>");
    khz_buf_puts(&sheet_xml, "</sheetData></worksheet>");
    if (sheet_xml.overflow != 0) return KHZ_SHEET_ERR_MEMORY;

    local.shared_strings = (uint64_t)sst.count;

    {
        size_t cap = (size_t)256;
        size_t term;
        if (!khz_size_mul(sst.count, (size_t)48, &term) || !khz_size_add(cap, term, &cap)) {
            return KHZ_SHEET_ERR_LIMIT;
        }
        if (text_bytes > (uint64_t)SIZE_MAX
            || !khz_size_mul((size_t)text_bytes, (size_t)6, &term)
            || !khz_size_add(cap, term, &cap)) {
            return KHZ_SHEET_ERR_LIMIT;
        }
        status = khz_buf_init(&sst_xml, scratch, cap);
        if (status != KHZ_SHEET_OK) return status;
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
            khz_buf_puts(&sst_xml, "<si><t xml:space=\"preserve\">");
            khz_buf_put_xml(&sst_xml, sst.texts[i], (size_t)sst.lens[i]);
            khz_buf_puts(&sst_xml, "</t></si>");
        }
        khz_buf_puts(&sst_xml, "</sst>");
        if (sst_xml.overflow != 0) return KHZ_SHEET_ERR_MEMORY;
    }

    status = khz_buf_init(&types_xml, scratch, (size_t)1024);
    if (status != KHZ_SHEET_OK) return status;
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
    if (status != KHZ_SHEET_OK) return status;
    khz_buf_puts(&root_rels,
                 "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n"
                 "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/"
                 "2006/relationships\">"
                 "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/"
                 "officeDocument/2006/relationships/officeDocument\" "
                 "Target=\"xl/workbook.xml\"/></Relationships>");

    status = khz_buf_init(&book_xml, scratch, (size_t)768);
    if (status != KHZ_SHEET_OK) return status;
    khz_buf_puts(&book_xml,
                 "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n"
                 "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/"
                 "2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/"
                 "officeDocument/2006/relationships\"><sheets><sheet name=\"");
    khz_buf_put_xml(&book_xml, name, name_len);
    khz_buf_puts(&book_xml, "\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");

    status = khz_buf_init(&book_rels, scratch, (size_t)768);
    if (status != KHZ_SHEET_OK) return status;
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

    entries[entry_count++] = (KhzZipEntry){ "[Content_Types].xml", types_xml.data, types_xml.len, 0u, 0u };
    entries[entry_count++] = (KhzZipEntry){ "_rels/.rels", root_rels.data, root_rels.len, 0u, 0u };
    entries[entry_count++] = (KhzZipEntry){ "xl/workbook.xml", book_xml.data, book_xml.len, 0u, 0u };
    entries[entry_count++] = (KhzZipEntry){ "xl/_rels/workbook.xml.rels", book_rels.data, book_rels.len, 0u, 0u };
    entries[entry_count++] = (KhzZipEntry){ "xl/worksheets/sheet1.xml", sheet_xml.data, sheet_xml.len, 0u, 0u };
    if (sst.count > (size_t)0) {
        entries[entry_count++] = (KhzZipEntry){ "xl/sharedStrings.xml", sst_xml.data, sst_xml.len, 0u, 0u };
    }

    {
        size_t package_cap = (size_t)534;
        for (i = (size_t)0; i < entry_count; ++i) {
            size_t name_bytes = strlen(entries[i].name);
            size_t term;
            if (entries[i].len > (size_t)UINT32_MAX || name_bytes > (size_t)UINT16_MAX) {
                return KHZ_SHEET_ERR_LIMIT;
            }
            if (!khz_size_mul(name_bytes, (size_t)2, &term)
                || !khz_size_add(term, (size_t)76, &term)
                || !khz_size_add(term, entries[i].len, &term)
                || !khz_size_add(package_cap, term, &package_cap)) {
                return KHZ_SHEET_ERR_LIMIT;
            }
        }
        if (package_cap > (size_t)UINT32_MAX) return KHZ_SHEET_ERR_LIMIT;
        status = khz_buf_init(&pkg, scratch, package_cap);
        if (status != KHZ_SHEET_OK) return status;
    }

    for (i = (size_t)0; i < entry_count; ++i) khz_zip_put_local(&pkg, &entries[i]);
    khz_zip_finish(&pkg, entries, entry_count);
    if (pkg.overflow != 0) return KHZ_SHEET_ERR_MEMORY;

    local.bytes_written = (uint64_t)pkg.len;
    local.arena_bytes_used = (uint64_t)(khz_arena_used(scratch) - arena_start);
    *bytes = pkg.data;
    *length = pkg.len;
    if (report != NULL) *report = local;
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

    if (sheet == NULL || scratch == NULL || path == NULL) return KHZ_SHEET_ERR_NULL;
    path_len = strlen(path);
    if (path_len == (size_t)0 || path_len > KHZ_XLSX_MAX_PATH) return KHZ_SHEET_ERR_RANGE;

    mark = khz_arena_mark(scratch);
    status = khz_xlsx_build(sheet, scratch, sheet_name, &bytes, &length, report);
    if (status != KHZ_SHEET_OK) {
        (void)khz_arena_release(scratch, mark);
        return status;
    }

    file = fopen(path, "wb");
    if (file == NULL) {
        (void)khz_arena_release(scratch, mark);
        return KHZ_SHEET_ERR_OS;
    }
    if (fwrite(bytes, (size_t)1, length, file) != length) {
        fclose(file);
        (void)khz_arena_release(scratch, mark);
        return KHZ_SHEET_ERR_OS;
    }
    if (fclose(file) != 0) {
        (void)khz_arena_release(scratch, mark);
        return KHZ_SHEET_ERR_OS;
    }

    (void)khz_arena_release(scratch, mark);
    return KHZ_SHEET_OK;
}
