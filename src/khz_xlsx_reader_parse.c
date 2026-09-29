/* khz_xlsx_reader_parse.c - part 2 of 2: the XML layer.
 *
 * Turns the parts located by khz_xlsx_reader.c into cells. Pairs with that
 * file; both are required at link.
 *
 * This is a bounded scanner, not a general XML parser. Element matching uses
 * local names so ordinary namespace prefixes do not change worksheet meaning.
 * Text decoding handles the five predefined entities, numeric character
 * references, CDATA sections and SpreadsheetML _xHHHH_ escapes where the
 * field is an escaped string. DTDs, entity declarations and full namespace
 * resolution remain outside the supported profile.
 *
 * Cell-local lexical faults become explicit spreadsheet error values when the
 * coordinate is known. Structural XML/package faults, resource exhaustion and
 * sheet-capacity failures still stop the import instead of guessing state.
 * * Every buffer comes from the reader's arena, inside the mark taken at load.
 * When that arena is separate scratch storage, khz_xlsx_reader_reset can hand
 * the load back. When it is also the destination sheet arena, parse_sheet
 * marks the reader non-releasable so reset cannot rewind live cell payloads.
 */

#include <string.h>

#include "khz_xlsx_reader.h"
#include "khz_formula.h"

/* Ceiling on one decoded string. Long enough for any label, bounded so a
   hostile part cannot ask for an unbounded arena block. */
#define KHZ_XLSX_MAX_TEXT ((size_t)1 << 16)

/* Denominator ceiling when converting decimal text to an exact rational.
   10^18 fits int64; a further digit would not. */
#define KHZ_XLSX_MAX_SCALE ((int64_t)1000000000000000000)

/* A nonzero rational whose numerator and denominator both fit int64 cannot
   survive more than a few dozen decimal shifts. Capping the work at 38 keeps
   hostile exponents bounded; representability is still decided by the exact
   rational multiply below, not by this coarse limit. */
#define KHZ_XLSX_MAX_EXPONENT_STEPS ((uint32_t)38)

/* Sentinel outside every valid arena mark. khz_arena_release rejects marks
   greater than the current offset without changing the arena. This lets the
   existing reset path fail closed when reader storage and live sheet storage
   share one arena, without changing the public reader layout. */
#define KHZ_XLSX_SHARED_ARENA_MARK ((size_t)-1)

static const char *khz_xml_find(const char *hay, size_t hay_len, const char *needle)
{
	size_t needle_len = strlen(needle);
	size_t at;

	if (hay == NULL || needle_len == 0 || hay_len < needle_len) {
		return NULL;
	}

	for (at = 0; at + needle_len <= hay_len; ++at) {
		if (memcmp(hay + at, needle, needle_len) == 0) {
			return hay + at;
		}
	}

	return NULL;
}

/* Reads attr="value" out of one element's open tag. The search is confined to
   tag_len so a match cannot be picked up from the following element. */
static int khz_xml_is_space(char ch)
{
    return ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n';
}

static const char *khz_xml_tag_end(const char *open, const char *limit)
{
    char quote = '\0';
    const char *p;

    if (open == NULL || limit == NULL || open >= limit || *open != '<') return NULL;
    for (p = open + 1; p < limit; ++p) {
        if (quote != '\0') {
            if (*p == quote) quote = '\0';
            continue;
        }
        if (*p == '"' || *p == '\'') {
            quote = *p;
        } else if (*p == '>') {
            return p;
        }
    }
    return NULL;
}

static const char *khz_xml_find_tag_local(const char *hay, size_t hay_len,
                                          const char *local, int closing)
{
    const char *cursor;
    const char *limit;
    size_t local_len;

    if (hay == NULL || local == NULL) return NULL;
    local_len = strlen(local);
    if (local_len == 0u) return NULL;
    cursor = hay;
    limit = hay + hay_len;

    while (cursor < limit) {
        const char *open = (const char *)memchr(cursor, '<', (size_t)(limit - cursor));
        const char *p;
        const char *name_start;
        const char *name_end;
        const char *local_start;
        const char *q;

        if (open == NULL || open + 1 >= limit) return NULL;
        p = open + 1;

        if ((size_t)(limit - p) >= 3u && memcmp(p, "!--", 3u) == 0) {
            const char *end = khz_xml_find(p + 3, (size_t)(limit - (p + 3)), "-->");
            if (end == NULL) return NULL;
            cursor = end + 3;
            continue;
        }
        if ((size_t)(limit - p) >= 8u && memcmp(p, "![CDATA[", 8u) == 0) {
            const char *end = khz_xml_find(p + 8, (size_t)(limit - (p + 8)), "]]>");
            if (end == NULL) return NULL;
            cursor = end + 3;
            continue;
        }
        if (*p == '?') {
            const char *end = khz_xml_find(p + 1, (size_t)(limit - (p + 1)), "?>");
            if (end == NULL) return NULL;
            cursor = end + 2;
            continue;
        }
        if (*p == '!') {
            const char *end = (const char *)memchr(p, '>', (size_t)(limit - p));
            if (end == NULL) return NULL;
            cursor = end + 1;
            continue;
        }

        if (closing != 0) {
            if (*p != '/') { cursor = p; continue; }
            ++p;
        } else if (*p == '/') {
            cursor = p + 1;
            continue;
        }

        name_start = p;
        while (p < limit && !khz_xml_is_space(*p) && *p != '>' && *p != '/') ++p;
        name_end = p;
        local_start = name_start;
        for (q = name_start; q < name_end; ++q) if (*q == ':') local_start = q + 1;

        if ((size_t)(name_end - local_start) == local_len
            && memcmp(local_start, local, local_len) == 0) {
            return open;
        }
        cursor = p;
    }
    return NULL;
}

/* Reads attr="value" or attr='value' out of one element's open tag. */
static int khz_xml_attr(const char *tag, size_t tag_len, const char *name,
                        const char **value, size_t *value_len)
{
    size_t name_len = strlen(name);
    size_t at;

    *value = NULL;
    *value_len = 0u;
    for (at = 0u; at + name_len < tag_len; ++at) {
        size_t p;
        size_t start;
        char quote;
        if (memcmp(tag + at, name, name_len) != 0) continue;
        if (at != 0u && !khz_xml_is_space(tag[at - 1])) continue;
        p = at + name_len;
        if (p < tag_len && !khz_xml_is_space(tag[p]) && tag[p] != '=') continue;
        while (p < tag_len && khz_xml_is_space(tag[p])) ++p;
        if (p >= tag_len || tag[p] != '=') continue;
        ++p;
        while (p < tag_len && khz_xml_is_space(tag[p])) ++p;
        if (p >= tag_len || (tag[p] != '"' && tag[p] != '\'')) continue;
        quote = tag[p++];
        start = p;
        while (p < tag_len && tag[p] != quote) ++p;
        if (p >= tag_len) return 0;
        *value = tag + start;
        *value_len = p - start;
        return 1;
    }
    return 0;
}

static int khz_xml_attr_local(const char *tag, size_t tag_len, const char *local,
                              const char **value, size_t *value_len)
{
    size_t at = 1u;
    size_t local_len = strlen(local);

    *value = NULL;
    *value_len = 0u;
    while (at < tag_len && !khz_xml_is_space(tag[at]) && tag[at] != '>') ++at;
    while (at < tag_len) {
        size_t name_start, name_end, local_start, p, start;
        char quote;
        while (at < tag_len && khz_xml_is_space(tag[at])) ++at;
        if (at >= tag_len || tag[at] == '/' || tag[at] == '>') break;
        name_start = at;
        while (at < tag_len && !khz_xml_is_space(tag[at]) && tag[at] != '='
               && tag[at] != '/' && tag[at] != '>') ++at;
        name_end = at;
        local_start = name_start;
        for (p = name_start; p < name_end; ++p) if (tag[p] == ':') local_start = p + 1u;
        while (at < tag_len && khz_xml_is_space(tag[at])) ++at;
        if (at >= tag_len || tag[at] != '=') break;
        ++at;
        while (at < tag_len && khz_xml_is_space(tag[at])) ++at;
        if (at >= tag_len || (tag[at] != '"' && tag[at] != '\'')) break;
        quote = tag[at++];
        start = at;
        while (at < tag_len && tag[at] != quote) ++at;
        if (at >= tag_len) return 0;
        if ((size_t)(name_end - local_start) == local_len
            && memcmp(tag + local_start, local, local_len) == 0) {
            *value = tag + start;
            *value_len = at - start;
            return 1;
        }
        ++at;
    }
    return 0;
}

static int khz_hex_value(char ch)
{
    if (ch >= '0' && ch <= '9') return ch - '0';
    if (ch >= 'a' && ch <= 'f') return ch - 'a' + 10;
    if (ch >= 'A' && ch <= 'F') return ch - 'A' + 10;
    return -1;
}

static int khz_xml_scalar_allowed(uint32_t cp)
{
    return cp == 0x09u || cp == 0x0au || cp == 0x0du
        || (cp >= 0x20u && cp <= 0xd7ffu)
        || (cp >= 0xe000u && cp <= 0xfffdu)
        || (cp >= 0x10000u && cp <= 0x10ffffu);
}

static KhzSheetStatus khz_utf8_emit(char *buffer, size_t capacity,
                                    size_t *write_at, uint32_t cp)
{
    size_t need;
    if (!khz_xml_scalar_allowed(cp)) return KHZ_SHEET_ERR_FORMAT;
    need = cp < 0x80u ? 1u : (cp < 0x800u ? 2u : (cp < 0x10000u ? 3u : 4u));
    if (*write_at > capacity || need > capacity - *write_at) return KHZ_SHEET_ERR_LIMIT;
    if (need == 1u) buffer[(*write_at)++] = (char)cp;
    else if (need == 2u) {
        buffer[(*write_at)++] = (char)(0xc0u | (cp >> 6));
        buffer[(*write_at)++] = (char)(0x80u | (cp & 0x3fu));
    } else if (need == 3u) {
        buffer[(*write_at)++] = (char)(0xe0u | (cp >> 12));
        buffer[(*write_at)++] = (char)(0x80u | ((cp >> 6) & 0x3fu));
        buffer[(*write_at)++] = (char)(0x80u | (cp & 0x3fu));
    } else {
        buffer[(*write_at)++] = (char)(0xf0u | (cp >> 18));
        buffer[(*write_at)++] = (char)(0x80u | ((cp >> 12) & 0x3fu));
        buffer[(*write_at)++] = (char)(0x80u | ((cp >> 6) & 0x3fu));
        buffer[(*write_at)++] = (char)(0x80u | (cp & 0x3fu));
    }
    return KHZ_SHEET_OK;
}

static int khz_xstring_unit(const char *src, size_t left, uint32_t *unit)
{
    uint32_t value = 0u;
    size_t i;
    if (left < 7u || src[0] != '_' || src[1] != 'x' || src[6] != '_') return 0;
    for (i = 0u; i < 4u; ++i) {
        int hex = khz_hex_value(src[2u + i]);
        if (hex < 0) return 0;
        value = (value << 4) | (uint32_t)hex;
    }
    *unit = value;
    return 1;
}

static KhzSheetStatus khz_xml_decode(KhzArena *arena, const char *src, size_t len,
                                     int decode_xstring, char **out, size_t *out_len)
{
    char *buffer;
    size_t read_at = 0u, write_at = 0u, mark;
    int in_cdata = 0;
    KhzSheetStatus status = KHZ_SHEET_OK;

    if (arena == NULL || src == NULL || out == NULL || out_len == NULL) return KHZ_SHEET_ERR_NULL;
    if (len > KHZ_XLSX_MAX_TEXT) return KHZ_SHEET_ERR_LIMIT;
    mark = khz_arena_mark(arena);
    buffer = (char *)khz_arena_alloc(arena, len + 1u);
    if (buffer == NULL) return KHZ_SHEET_ERR_MEMORY;

    while (read_at < len) {
        size_t left = len - read_at;
        if (in_cdata == 0 && left >= 9u && memcmp(src + read_at, "<![CDATA[", 9u) == 0) {
            in_cdata = 1; read_at += 9u; continue;
        }
        if (in_cdata != 0 && left >= 3u && memcmp(src + read_at, "]]>", 3u) == 0) {
            in_cdata = 0; read_at += 3u; continue;
        }
        if (decode_xstring != 0) {
            uint32_t first;
            if (khz_xstring_unit(src + read_at, left, &first)) {
                uint32_t cp = first;
                size_t consumed = 7u;
                if (first >= 0xd800u && first <= 0xdbffu) {
                    uint32_t second;
                    if (left < 14u || !khz_xstring_unit(src + read_at + 7u, left - 7u, &second)
                        || second < 0xdc00u || second > 0xdfffu) { status = KHZ_SHEET_ERR_FORMAT; goto fail; }
                    cp = 0x10000u + ((first - 0xd800u) << 10) + (second - 0xdc00u);
                    consumed = 14u;
                } else if (first >= 0xdc00u && first <= 0xdfffu) {
                    status = KHZ_SHEET_ERR_FORMAT; goto fail;
                }
                status = khz_utf8_emit(buffer, len, &write_at, cp);
                if (status != KHZ_SHEET_OK) goto fail;
                read_at += consumed;
                continue;
            }
        }
        if (in_cdata == 0 && src[read_at] == '&') {
            if (left >= 5u && memcmp(src + read_at, "&amp;", 5u) == 0) {
                buffer[write_at++] = '&'; read_at += 5u; continue;
            }
            if (left >= 4u && memcmp(src + read_at, "&lt;", 4u) == 0) {
                buffer[write_at++] = '<'; read_at += 4u; continue;
            }
            if (left >= 4u && memcmp(src + read_at, "&gt;", 4u) == 0) {
                buffer[write_at++] = '>'; read_at += 4u; continue;
            }
            if (left >= 6u && memcmp(src + read_at, "&quot;", 6u) == 0) {
                buffer[write_at++] = '"'; read_at += 6u; continue;
            }
            if (left >= 6u && memcmp(src + read_at, "&apos;", 6u) == 0) {
                buffer[write_at++] = '\''; read_at += 6u; continue;
            }
            if (left >= 4u && src[read_at + 1u] == '#') {
                size_t p = read_at + 2u;
                uint32_t cp = 0u, base = 10u;
                size_t digits = 0u;
                if (p < len && (src[p] == 'x' || src[p] == 'X')) { base = 16u; ++p; }
                while (p < len && src[p] != ';') {
                    int digit = base == 16u ? khz_hex_value(src[p])
                                            : (src[p] >= '0' && src[p] <= '9' ? src[p] - '0' : -1);
                    if (digit < 0 || cp > (0x10ffffu - (uint32_t)digit) / base) {
                        status = KHZ_SHEET_ERR_FORMAT; goto fail;
                    }
                    cp = cp * base + (uint32_t)digit;
                    ++digits; ++p;
                }
                if (digits == 0u || p >= len || src[p] != ';') { status = KHZ_SHEET_ERR_FORMAT; goto fail; }
                status = khz_utf8_emit(buffer, len, &write_at, cp);
                if (status != KHZ_SHEET_OK) goto fail;
                read_at = p + 1u;
                continue;
            }
            status = KHZ_SHEET_ERR_FORMAT;
            goto fail;
        }
        buffer[write_at++] = src[read_at++];
    }
    if (in_cdata != 0) { status = KHZ_SHEET_ERR_FORMAT; goto fail; }
    buffer[write_at] = '\0';
    *out = buffer;
    *out_len = write_at;
    return KHZ_SHEET_OK;

fail:
    (void)khz_arena_release(arena, mark);
    return status;
}
static KhzSheetStatus khz_xlsx_apply_exponent(KhzRational value,
                                               int exponent_negative,
                                               uint32_t exponent,
                                               KhzRational *out)
{
	KhzRational factor;
	KhzSheetStatus status;

	if (out == NULL) return KHZ_SHEET_ERR_NULL;
	if (value.num == (int64_t)0 || exponent == (uint32_t)0) {
		*out = value;
		return KHZ_SHEET_OK;
	}

	factor.num = exponent_negative != 0 ? (int64_t)1 : (int64_t)10;
	factor.den = exponent_negative != 0 ? (int64_t)10 : (int64_t)1;

	while (exponent != (uint32_t)0) {
		status = khz_rational_mul(value, factor, &value);
		if (status != KHZ_SHEET_OK) return status;
		exponent -= (uint32_t)1;
	}

	*out = value;
	return KHZ_SHEET_OK;
}

static KhzSheetStatus khz_xlsx_parse_number(const char *text, size_t len, KhzRational *out)
{
	int64_t mantissa = 0;
	int64_t scale = 1;
	KhzRational value;
	KhzSheetStatus status;
	size_t at = 0;
	uint32_t exponent = 0u;
	int negative = 0;
	int seen_digit = 0;
	int seen_point = 0;
	int seen_exponent = 0;
	int exponent_negative = 0;
	int exponent_digit = 0;
	int exponent_too_large = 0;

	if (text == NULL || out == NULL) return KHZ_SHEET_ERR_NULL;
	if (len == 0) return KHZ_SHEET_ERR_FORMAT;

	if (text[at] == '-') { negative = 1; at += 1u; }
	else if (text[at] == '+') { at += 1u; }

	for (; at < len; ++at) {
		char ch = text[at];
		if (ch == 'e' || ch == 'E') {
			seen_exponent = 1;
			at += 1u;
			break;
		}
		if (ch == '.') {
			if (seen_point != 0) return KHZ_SHEET_ERR_FORMAT;
			seen_point = 1;
			continue;
		}
		if (ch < '0' || ch > '9') return KHZ_SHEET_ERR_FORMAT;
		if (mantissa > (INT64_MAX - 9) / 10) return KHZ_SHEET_ERR_OVERFLOW;
		mantissa = mantissa * 10 + (int64_t)(ch - '0');
		seen_digit = 1;
		if (seen_point != 0) {
			if (scale > KHZ_XLSX_MAX_SCALE / 10) return KHZ_SHEET_ERR_OVERFLOW;
			scale *= 10;
		}
	}

	if (seen_digit == 0) return KHZ_SHEET_ERR_FORMAT;

	if (seen_exponent != 0) {
		if (at >= len) return KHZ_SHEET_ERR_FORMAT;
		if (text[at] == '-' || text[at] == '+') {
			exponent_negative = text[at] == '-' ? 1 : 0;
			at += 1u;
		}
		if (at >= len) return KHZ_SHEET_ERR_FORMAT;

		for (; at < len; ++at) {
			char ch = text[at];
			uint32_t digit;
			if (ch < '0' || ch > '9') return KHZ_SHEET_ERR_FORMAT;
			exponent_digit = 1;
			digit = (uint32_t)(ch - '0');
			if (exponent_too_large == 0) {
				if (exponent > (KHZ_XLSX_MAX_EXPONENT_STEPS - digit) / (uint32_t)10) {
					exponent_too_large = 1;
				} else {
					exponent = exponent * (uint32_t)10 + digit;
				}
			}
		}
		if (exponent_digit == 0) return KHZ_SHEET_ERR_FORMAT;
	}

	if (negative != 0) mantissa = -mantissa;
	status = khz_rational_make(mantissa, scale, &value);
	if (status != KHZ_SHEET_OK) return status;
	if (value.num == (int64_t)0) { *out = value; return KHZ_SHEET_OK; }
	if (exponent_too_large != 0) return KHZ_SHEET_ERR_OVERFLOW;
	return khz_xlsx_apply_exponent(value, exponent_negative, exponent, out);
}

KhzSheetStatus khz_xlsx_reader_check_package(KhzXlsxReader *reader)
{
	const KhzXlsxEntry *entry;
	KhzSheetStatus status;

	if (reader == NULL) return KHZ_SHEET_ERR_NULL;
	if (reader->loaded == 0) return KHZ_SHEET_ERR_STATE;

	status = khz_xlsx_reader_find(reader, "[Content_Types].xml", &entry);
	if (status != KHZ_SHEET_OK) return status;
	status = khz_xlsx_reader_find(reader, "_rels/.rels", &entry);
	if (status != KHZ_SHEET_OK) return status;
	status = khz_xlsx_reader_find(reader, "xl/workbook.xml", &entry);
	if (status != KHZ_SHEET_OK) return status;
	status = khz_xlsx_reader_find(reader, "xl/_rels/workbook.xml.rels", &entry);
	if (status != KHZ_SHEET_OK) return status;

	status = khz_xlsx_reader_find(reader, "xl/workbook.xml", &entry);
	if (status != KHZ_SHEET_OK) return status;
	if (khz_xml_find_tag_local((const char *)entry->data, entry->size, "sheet", 0) == NULL) {
		return KHZ_SHEET_ERR_FORMAT;
	}
	return KHZ_SHEET_OK;
}

KhzSheetStatus khz_xlsx_reader_shared_strings(KhzXlsxReader *reader)
{
    const KhzXlsxEntry *entry;
    const char *part, *cursor, *limit;
    const char **table;
    size_t counted = 0u, index = 0u;
    KhzSheetStatus status;

    if (reader == NULL) return KHZ_SHEET_ERR_NULL;
    if (reader->loaded == 0) return KHZ_SHEET_ERR_STATE;
    status = khz_xlsx_reader_find(reader, "xl/sharedStrings.xml", &entry);
    if (status == KHZ_SHEET_ERR_MISSING) {
        reader->strings = NULL;
        reader->string_count = 0u;
        return KHZ_SHEET_OK;
    }
    if (status != KHZ_SHEET_OK) return status;

    part = (const char *)entry->data;
    limit = part + entry->size;
    cursor = part;
    while (cursor < limit) {
        const char *item = khz_xml_find_tag_local(cursor, (size_t)(limit - cursor), "si", 0);
        const char *tag_end;
        if (item == NULL) break;
        tag_end = khz_xml_tag_end(item, limit);
        if (tag_end == NULL) return KHZ_SHEET_ERR_FORMAT;
        if (++counted > KHZ_XLSX_READER_MAX_STRINGS) return KHZ_SHEET_ERR_LIMIT;
        cursor = tag_end + 1;
    }
    if (counted == 0u) {
        reader->strings = NULL;
        reader->string_count = 0u;
        return KHZ_SHEET_OK;
    }

    table = (const char **)khz_arena_alloc_zeroed(reader->arena, counted * sizeof *table);
    if (table == NULL) return KHZ_SHEET_ERR_MEMORY;

    cursor = part;
    while (index < counted && cursor < limit) {
        const char *item = khz_xml_find_tag_local(cursor, (size_t)(limit - cursor), "si", 0);
        const char *item_tag_end;
        const char *item_end;
        const char *open;
        const char *open_end;
        const char *close;
        char *decoded;
        size_t decoded_len;

        if (item == NULL) break;
        item_tag_end = khz_xml_tag_end(item, limit);
        if (item_tag_end == NULL) return KHZ_SHEET_ERR_FORMAT;
        item_end = khz_xml_find_tag_local(item_tag_end + 1,
            (size_t)(limit - (item_tag_end + 1)), "si", 1);
        if (item_end == NULL) return KHZ_SHEET_ERR_FORMAT;
        open = khz_xml_find_tag_local(item_tag_end + 1,
            (size_t)(item_end - (item_tag_end + 1)), "t", 0);
        if (open == NULL) {
            table[index++] = "";
            cursor = item_end + 1;
            continue;
        }
        open_end = khz_xml_tag_end(open, item_end);
        if (open_end == NULL) return KHZ_SHEET_ERR_FORMAT;
        close = khz_xml_find_tag_local(open_end + 1,
            (size_t)(item_end - (open_end + 1)), "t", 1);
        if (close == NULL) return KHZ_SHEET_ERR_FORMAT;
        status = khz_xml_decode(reader->arena, open_end + 1,
            (size_t)(close - (open_end + 1)), 1, &decoded, &decoded_len);
        if (status == KHZ_SHEET_ERR_FORMAT) {
            table[index++] = NULL;
            cursor = item_end + 1;
            continue;
        }
        if (status != KHZ_SHEET_OK) return status;
        table[index++] = decoded;
        cursor = item_end + 1;
    }

    reader->strings = table;
    reader->string_count = index;
    reader->report.shared_strings = (uint64_t)index;
    return KHZ_SHEET_OK;
}
static KhzSheetStatus khz_xlsx_parse_row_number(const char *text, size_t len,
                                                uint32_t *row)
{
    uint64_t value = 0u;
    size_t i;
    if (text == NULL || row == NULL) return KHZ_SHEET_ERR_NULL;
    if (len == 0u) return KHZ_SHEET_ERR_FORMAT;
    for (i = 0u; i < len; ++i) {
        uint64_t digit;
        if (text[i] < '0' || text[i] > '9') return KHZ_SHEET_ERR_FORMAT;
        digit = (uint64_t)(text[i] - '0');
        if (value > ((uint64_t)KHZ_GRID_MAX_ROWS - digit) / (uint64_t)10) {
            return KHZ_SHEET_ERR_LIMIT;
        }
        value = value * (uint64_t)10 + digit;
    }
    if (value == 0u || value > (uint64_t)KHZ_GRID_MAX_ROWS) return KHZ_SHEET_ERR_LIMIT;
    *row = (uint32_t)(value - 1u);
    return KHZ_SHEET_OK;
}

static KhzCellError khz_xlsx_error_literal(const char *text, size_t len)
{
    if (len == 6u && memcmp(text, "#NULL!", 6u) == 0) return KHZ_CELL_ERROR_NULL;
    if (len == 7u && memcmp(text, "#DIV/0!", 7u) == 0) return KHZ_CELL_ERROR_DIV0;
    if (len == 7u && memcmp(text, "#VALUE!", 7u) == 0) return KHZ_CELL_ERROR_VALUE;
    if (len == 5u && memcmp(text, "#REF!", 5u) == 0) return KHZ_CELL_ERROR_REF;
    if (len == 6u && memcmp(text, "#NAME?", 6u) == 0) return KHZ_CELL_ERROR_NAME;
    if (len == 5u && memcmp(text, "#NUM!", 5u) == 0) return KHZ_CELL_ERROR_NUM;
    if (len == 4u && memcmp(text, "#N/A", 4u) == 0) return KHZ_CELL_ERROR_NA;
    return KHZ_CELL_ERROR_VALUE;
}

static KhzCellError khz_xlsx_fault_error(KhzSheetStatus status)
{
    switch (status) {
    case KHZ_SHEET_ERR_OVERFLOW:
    case KHZ_SHEET_ERR_RANGE:
        return KHZ_CELL_ERROR_NUM;
    case KHZ_SHEET_ERR_DIVZERO:
        return KHZ_CELL_ERROR_DIV0;
    case KHZ_SHEET_ERR_MISSING:
        return KHZ_CELL_ERROR_REF;
    default:
        return KHZ_CELL_ERROR_VALUE;
    }
}

static KhzSheetStatus khz_xlsx_record_cell_fault(KhzXlsxReader *reader, KhzSheet *sheet,
                                                  uint32_t col, uint32_t row,
                                                  KhzSheetStatus cause)
{
    KhzSheetStatus status = khz_sheet_set_error(sheet, col, row, khz_xlsx_fault_error(cause));
    if (status != KHZ_SHEET_OK) return status;
    reader->report.cells_unsupported += 1u;
    reader->report.cells_loaded += 1u;
    return KHZ_SHEET_OK;
}

static int khz_xlsx_formula_parse_fault(KhzSheetStatus status)
{
    return status == KHZ_SHEET_ERR_MISSING || status == KHZ_SHEET_ERR_FORMAT
        || status == KHZ_SHEET_ERR_OVERFLOW || status == KHZ_SHEET_ERR_RANGE;
}

static KhzSheetStatus khz_xlsx_inline_text(KhzXlsxReader *reader,
                                           const char *body, const char *body_end,
                                           char **decoded, size_t *decoded_len)
{
    const char *is_open;
    const char *is_end;
    const char *t_open;
    const char *t_open_end;
    const char *t_close;

    is_open = khz_xml_find_tag_local(body, (size_t)(body_end - body), "is", 0);
    if (is_open == NULL) return KHZ_SHEET_ERR_FORMAT;
    is_end = khz_xml_tag_end(is_open, body_end);
    if (is_end == NULL) return KHZ_SHEET_ERR_FORMAT;
    t_open = khz_xml_find_tag_local(is_end + 1, (size_t)(body_end - (is_end + 1)), "t", 0);
    if (t_open == NULL) {
        *decoded = NULL;
        *decoded_len = 0u;
        return KHZ_SHEET_OK;
    }
    t_open_end = khz_xml_tag_end(t_open, body_end);
    if (t_open_end == NULL) return KHZ_SHEET_ERR_FORMAT;
    t_close = khz_xml_find_tag_local(t_open_end + 1,
        (size_t)(body_end - (t_open_end + 1)), "t", 1);
    if (t_close == NULL) return KHZ_SHEET_ERR_FORMAT;
    return khz_xml_decode(reader->arena, t_open_end + 1,
        (size_t)(t_close - (t_open_end + 1)), 1, decoded, decoded_len);
}

KhzSheetStatus khz_xlsx_reader_parse_sheet(KhzXlsxReader *reader, KhzSheet *sheet,
                                           const char *part_name)
{
    const KhzXlsxEntry *entry;
    const char *row_cursor;
    const char *limit;
    uint32_t implicit_row = 0u;
    KhzSheetStatus status;

    if (reader == NULL || sheet == NULL || part_name == NULL) return KHZ_SHEET_ERR_NULL;
    if (reader->loaded == 0) return KHZ_SHEET_ERR_STATE;
    status = khz_xlsx_reader_find(reader, part_name, &entry);
    if (status != KHZ_SHEET_OK) return status;

    if (reader->arena == khz_sheet_arena(sheet)) reader->mark = KHZ_XLSX_SHARED_ARENA_MARK;
    row_cursor = (const char *)entry->data;
    limit = row_cursor + entry->size;

    while (row_cursor < limit) {
        const char *row_open = khz_xml_find_tag_local(row_cursor,
            (size_t)(limit - row_cursor), "row", 0);
        const char *row_tag_end;
        const char *row_close;
        const char *row_close_end;
        const char *row_ref;
        size_t row_ref_len;
        uint32_t row;
        uint32_t next_col = 0u;
        const char *cell_cursor;

        if (row_open == NULL) break;
        row_tag_end = khz_xml_tag_end(row_open, limit);
        if (row_tag_end == NULL) return KHZ_SHEET_ERR_FORMAT;

        if (khz_xml_attr(row_open, (size_t)(row_tag_end - row_open),
                         "r", &row_ref, &row_ref_len) != 0) {
            status = khz_xlsx_parse_row_number(row_ref, row_ref_len, &row);
            if (status != KHZ_SHEET_OK) return status;
        } else {
            if (implicit_row >= KHZ_GRID_MAX_ROWS) return KHZ_SHEET_ERR_LIMIT;
            row = implicit_row;
        }
        implicit_row = row + 1u;

        if (row_tag_end > row_open && *(row_tag_end - 1) == '/') {
            row_cursor = row_tag_end + 1;
            continue;
        }

        row_close = khz_xml_find_tag_local(row_tag_end + 1,
            (size_t)(limit - (row_tag_end + 1)), "row", 1);
        if (row_close == NULL) return KHZ_SHEET_ERR_FORMAT;
        row_close_end = khz_xml_tag_end(row_close, limit);
        if (row_close_end == NULL) return KHZ_SHEET_ERR_FORMAT;
        cell_cursor = row_tag_end + 1;

        while (cell_cursor < row_close) {
            const char *open = khz_xml_find_tag_local(cell_cursor,
                (size_t)(row_close - cell_cursor), "c", 0);
            const char *tag_end;
            const char *body_end;
            const char *body_close_end;
            const char *ref;
            const char *type;
            const char *value_open;
            const char *value_open_end;
            const char *value_close;
            const char *formula_open;
            size_t tag_len, ref_len, type_len;
            uint32_t col = 0u, cell_row = row;
            int self_closing;

            if (open == NULL) break;
            tag_end = khz_xml_tag_end(open, row_close);
            if (tag_end == NULL) return KHZ_SHEET_ERR_FORMAT;
            tag_len = (size_t)(tag_end - open);
            self_closing = tag_end > open && *(tag_end - 1) == '/';
            reader->report.cells_seen += 1u;

            if (khz_xml_attr(open, tag_len, "r", &ref, &ref_len) != 0) {
                status = khz_xlsx_reader_parse_ref(ref, ref_len, &col, &cell_row);
                if (status != KHZ_SHEET_OK) {
                    reader->report.cells_unsupported += 1u;
                    if (next_col < KHZ_GRID_MAX_COLUMNS) ++next_col;
                    cell_cursor = tag_end + 1;
                    continue;
                }
                next_col = col < KHZ_GRID_MAX_COLUMNS - 1u ? col + 1u : KHZ_GRID_MAX_COLUMNS;
            } else {
                if (next_col >= KHZ_GRID_MAX_COLUMNS) {
                    reader->report.cells_unsupported += 1u;
                    cell_cursor = tag_end + 1;
                    continue;
                }
                col = next_col++;
                cell_row = row;
            }

            if (self_closing) {
                cell_cursor = tag_end + 1;
                continue;
            }

            body_end = khz_xml_find_tag_local(tag_end + 1,
                (size_t)(row_close - (tag_end + 1)), "c", 1);
            if (body_end == NULL) return KHZ_SHEET_ERR_FORMAT;
            body_close_end = khz_xml_tag_end(body_end, row_close);
            if (body_close_end == NULL) return KHZ_SHEET_ERR_FORMAT;
            if (khz_xml_attr(open, tag_len, "t", &type, &type_len) == 0) {
                type = NULL;
                type_len = 0u;
            }

            formula_open = khz_xml_find_tag_local(tag_end + 1,
                (size_t)(body_end - (tag_end + 1)), "f", 0);
            if (formula_open != NULL) {
                const char *formula_open_end = khz_xml_tag_end(formula_open, body_end);
                const char *formula_close = NULL;
                char *decoded = NULL;
                size_t decoded_len = 0u;
                int formula_self_closing;

                reader->report.formulas_seen += 1u;
                if (formula_open_end == NULL) return KHZ_SHEET_ERR_FORMAT;
                formula_self_closing = formula_open_end > formula_open
                    && *(formula_open_end - 1) == '/';
                if (!formula_self_closing) {
                    formula_close = khz_xml_find_tag_local(formula_open_end + 1,
                        (size_t)(body_end - (formula_open_end + 1)), "f", 1);
                    if (formula_close == NULL) return KHZ_SHEET_ERR_FORMAT;
                    status = khz_xml_decode(reader->arena, formula_open_end + 1,
                        (size_t)(formula_close - (formula_open_end + 1)), 0,
                        &decoded, &decoded_len);
                    if (status == KHZ_SHEET_ERR_FORMAT) {
                        status = khz_xlsx_record_cell_fault(reader, sheet, col, cell_row, status);
                        if (status != KHZ_SHEET_OK) return status;
                        cell_cursor = body_close_end + 1;
                        continue;
                    }
                    if (status != KHZ_SHEET_OK) return status;
                }

                if (decoded_len == 0u) {
                    status = khz_sheet_set_error(sheet, col, cell_row, KHZ_CELL_ERROR_NAME);
                    if (status != KHZ_SHEET_OK) return status;
                    reader->report.cells_unsupported += 1u;
                    reader->report.cells_loaded += 1u;
                    cell_cursor = body_close_end + 1;
                    continue;
                }

                status = khz_formula_set(sheet, col, cell_row, decoded, decoded_len, NULL);
                if (status != KHZ_SHEET_OK) {
                    if (!khz_xlsx_formula_parse_fault(status)) return status;
                    status = khz_sheet_set_formula(sheet, col, cell_row, decoded, decoded_len);
                    if (status != KHZ_SHEET_OK) return status;
                    reader->report.cells_unsupported += 1u;
                }
                reader->report.cells_loaded += 1u;
                cell_cursor = body_close_end + 1;
                continue;
            }

            if (type != NULL && type_len == 9u && memcmp(type, "inlineStr", 9u) == 0) {
                char *decoded = NULL;
                size_t decoded_len = 0u;
                status = khz_xlsx_inline_text(reader, tag_end + 1, body_end,
                                              &decoded, &decoded_len);
                if (status == KHZ_SHEET_ERR_FORMAT) {
                    status = khz_xlsx_record_cell_fault(reader, sheet, col, cell_row, status);
                } else if (status == KHZ_SHEET_OK) {
                    status = khz_sheet_set_text(sheet, col, cell_row,
                                                decoded, decoded_len);
                    if (status == KHZ_SHEET_OK) reader->report.cells_loaded += 1u;
                }
                if (status != KHZ_SHEET_OK) return status;
                cell_cursor = body_close_end + 1;
                continue;
            }

            value_open = khz_xml_find_tag_local(tag_end + 1,
                (size_t)(body_end - (tag_end + 1)), "v", 0);
            if (value_open == NULL) {
                reader->report.cells_unsupported += 1u;
                cell_cursor = body_close_end + 1;
                continue;
            }
            value_open_end = khz_xml_tag_end(value_open, body_end);
            if (value_open_end == NULL) return KHZ_SHEET_ERR_FORMAT;
            value_close = khz_xml_find_tag_local(value_open_end + 1,
                (size_t)(body_end - (value_open_end + 1)), "v", 1);
            if (value_close == NULL) return KHZ_SHEET_ERR_FORMAT;

            if (type != NULL && type_len == 1u && type[0] == 's') {
                KhzRational slot;
                int64_t which;
                status = khz_xlsx_parse_number(value_open_end + 1,
                    (size_t)(value_close - (value_open_end + 1)), &slot);
                if (status == KHZ_SHEET_OK) status = khz_rational_to_i64(slot, &which);
                if (status != KHZ_SHEET_OK || which < 0 || reader->strings == NULL
                    || (size_t)which >= reader->string_count
                    || reader->strings[(size_t)which] == NULL) {
                    status = khz_xlsx_record_cell_fault(reader, sheet, col, cell_row,
                        status == KHZ_SHEET_OK ? KHZ_SHEET_ERR_FORMAT : status);
                } else {
                    status = khz_sheet_set_text(sheet, col, cell_row,
                        reader->strings[(size_t)which], strlen(reader->strings[(size_t)which]));
                    if (status == KHZ_SHEET_OK) reader->report.cells_loaded += 1u;
                }
            } else if (type != NULL && type_len == 3u && memcmp(type, "str", 3u) == 0) {
                char *decoded;
                size_t decoded_len;
                status = khz_xml_decode(reader->arena, value_open_end + 1,
                    (size_t)(value_close - (value_open_end + 1)), 1, &decoded, &decoded_len);
                if (status == KHZ_SHEET_ERR_FORMAT) {
                    status = khz_xlsx_record_cell_fault(reader, sheet, col, cell_row, status);
                } else if (status == KHZ_SHEET_OK) {
                    status = khz_sheet_set_text(sheet, col, cell_row, decoded, decoded_len);
                    if (status == KHZ_SHEET_OK) reader->report.cells_loaded += 1u;
                }
            } else if (type != NULL && type_len == 1u && type[0] == 'b') {
                size_t vlen = (size_t)(value_close - (value_open_end + 1));
                if (vlen != 1u || (value_open_end[1] != '0' && value_open_end[1] != '1')) {
                    status = khz_xlsx_record_cell_fault(reader, sheet, col, cell_row,
                                                        KHZ_SHEET_ERR_FORMAT);
                } else {
                    status = khz_sheet_set_bool(sheet, col, cell_row, value_open_end[1] == '1');
                    if (status == KHZ_SHEET_OK) reader->report.cells_loaded += 1u;
                }
            } else if (type != NULL && type_len == 1u && type[0] == 'e') {
                char *decoded;
                size_t decoded_len;
                status = khz_xml_decode(reader->arena, value_open_end + 1,
                    (size_t)(value_close - (value_open_end + 1)), 0, &decoded, &decoded_len);
                if (status == KHZ_SHEET_ERR_FORMAT) {
                    status = khz_xlsx_record_cell_fault(reader, sheet, col, cell_row, status);
                } else if (status == KHZ_SHEET_OK) {
                    status = khz_sheet_set_error(sheet, col, cell_row,
                                                 khz_xlsx_error_literal(decoded, decoded_len));
                    if (status == KHZ_SHEET_OK) reader->report.cells_loaded += 1u;
                }
            } else {
                KhzRational value;
                status = khz_xlsx_parse_number(value_open_end + 1,
                    (size_t)(value_close - (value_open_end + 1)), &value);
                if (status == KHZ_SHEET_ERR_FORMAT || status == KHZ_SHEET_ERR_OVERFLOW
                    || status == KHZ_SHEET_ERR_RANGE) {
                    status = khz_xlsx_record_cell_fault(reader, sheet, col, cell_row, status);
                } else if (status == KHZ_SHEET_OK) {
                    status = khz_sheet_set_rational(sheet, col, cell_row, value);
                    if (status == KHZ_SHEET_OK) reader->report.cells_loaded += 1u;
                }
            }

            if (status != KHZ_SHEET_OK) return status;
            cell_cursor = body_close_end + 1;
        }

        row_cursor = row_close_end + 1;
    }

    return KHZ_SHEET_OK;
}
static int khz_xlsx_relationship_type_is_worksheet(const char *type, size_t type_len)
{
	static const char suffix[] = "/worksheet";
	const size_t suffix_len = sizeof suffix - 1u;

	return type != NULL && type_len >= suffix_len
	    && memcmp(type + type_len - suffix_len, suffix, suffix_len) == 0;
}

static int khz_xlsx_part_target_is_safe(const char *target, size_t target_len)
{
	size_t segment = 0;
	size_t at;

	if (target == NULL || target_len == 0) return 0;

	for (at = 0; at <= target_len; ++at) {
		if (at < target_len) {
			char ch = target[at];
			if (ch == '\\' || ch == ':' || ch == '?' || ch == '#') return 0;
			if (ch != '/') continue;
		}

		if (at == segment) return 0;
		if (at - segment == 1u && target[segment] == '.') return 0;
		if (at - segment == 2u && target[segment] == '.' && target[segment + 1u] == '.') return 0;
		segment = at + 1u;
	}

	return 1;
}

static KhzSheetStatus khz_xlsx_workbook_target_to_part(const char *target,
                                                        size_t target_len,
                                                        char *out,
                                                        size_t capacity)
{
	static const char prefix[] = "xl/";
	const char *path = target;
	size_t path_len = target_len;
	size_t prefix_len = 0;

	if (target == NULL || out == NULL) return KHZ_SHEET_ERR_NULL;
	if (capacity == 0 || target_len == 0) return KHZ_SHEET_ERR_FORMAT;

	if (path[0] == '/') {
		path += 1;
		path_len -= 1u;
	} else {
		prefix_len = sizeof prefix - 1u;
	}

	if (!khz_xlsx_part_target_is_safe(path, path_len)) return KHZ_SHEET_ERR_UNSUPPORTED;
	if (prefix_len > capacity - 1u || path_len > capacity - 1u - prefix_len) {
		return KHZ_SHEET_ERR_LIMIT;
	}

	if (prefix_len != 0) memcpy(out, prefix, prefix_len);
	memcpy(out + prefix_len, path, path_len);
	out[prefix_len + path_len] = '\0';
	return KHZ_SHEET_OK;
}

static KhzSheetStatus khz_xlsx_reader_first_sheet_part(KhzXlsxReader *reader,
                                                        char *out,
                                                        size_t capacity)
{
	const KhzXlsxEntry *workbook;
	const KhzXlsxEntry *rels;
	const char *sheet;
	const char *sheet_end;
	const char *sheet_id;
	size_t sheet_id_len;
	const char *cursor;
	const char *limit;
	KhzSheetStatus status;

	if (reader == NULL || out == NULL) return KHZ_SHEET_ERR_NULL;
	if (reader->loaded == 0) return KHZ_SHEET_ERR_STATE;

	status = khz_xlsx_reader_find(reader, "xl/workbook.xml", &workbook);
	if (status != KHZ_SHEET_OK) return status;
	status = khz_xlsx_reader_find(reader, "xl/_rels/workbook.xml.rels", &rels);
	if (status != KHZ_SHEET_OK) return status;

	sheet = khz_xml_find_tag_local((const char *)workbook->data, workbook->size, "sheet", 0);
	if (sheet == NULL) return KHZ_SHEET_ERR_FORMAT;
	sheet_end = khz_xml_tag_end(sheet, (const char *)workbook->data + workbook->size);
	if (sheet_end == NULL) return KHZ_SHEET_ERR_FORMAT;
	if (khz_xml_attr_local(sheet, (size_t)(sheet_end - sheet), "id", &sheet_id, &sheet_id_len) == 0
	    || sheet_id_len == 0) {
		return KHZ_SHEET_ERR_FORMAT;
	}

	cursor = (const char *)rels->data;
	limit = cursor + rels->size;
	while (cursor < limit) {
		const char *rel = khz_xml_find_tag_local(cursor, (size_t)(limit - cursor), "Relationship", 0);
		const char *rel_end;
		const char *id;
		const char *type;
		const char *target;
		const char *target_mode;
		size_t rel_len;
		size_t id_len;
		size_t type_len;
		size_t target_len;
		size_t target_mode_len;
		char *decoded;
		size_t decoded_len;

		if (rel == NULL) break;
		rel_end = khz_xml_tag_end(rel, limit);
		if (rel_end == NULL) return KHZ_SHEET_ERR_FORMAT;
		rel_len = (size_t)(rel_end - rel);

		if (khz_xml_attr(rel, rel_len, "Id", &id, &id_len) != 0
		    && id_len == sheet_id_len && memcmp(id, sheet_id, id_len) == 0) {
			if (khz_xml_attr(rel, rel_len, "Type", &type, &type_len) == 0
			    || !khz_xlsx_relationship_type_is_worksheet(type, type_len)) {
				return KHZ_SHEET_ERR_FORMAT;
			}
			if (khz_xml_attr(rel, rel_len, "TargetMode", &target_mode, &target_mode_len) != 0) {
				if (target_mode_len != 8u || memcmp(target_mode, "Internal", 8u) != 0) {
					return KHZ_SHEET_ERR_UNSUPPORTED;
				}
			}
			if (khz_xml_attr(rel, rel_len, "Target", &target, &target_len) == 0
			    || target_len == 0) {
				return KHZ_SHEET_ERR_FORMAT;
			}
			status = khz_xml_decode(reader->arena, target, target_len, 0, &decoded, &decoded_len);
			if (status != KHZ_SHEET_OK) return status;
			return khz_xlsx_workbook_target_to_part(decoded, decoded_len, out, capacity);
		}

		cursor = rel_end + 1;
	}

	return KHZ_SHEET_ERR_MISSING;
}

KhzSheetStatus khz_xlsx_reader_read(KhzXlsxReader *reader, KhzSheet *sheet,
                                    const void *bytes, size_t size)
{
	char part_name[KHZ_XLSX_READER_MAX_NAME + 1u];
	KhzSheetStatus status;

	if (reader == NULL || sheet == NULL || bytes == NULL) return KHZ_SHEET_ERR_NULL;
	status = khz_xlsx_reader_load(reader, bytes, size);
	if (status != KHZ_SHEET_OK) return status;
	status = khz_xlsx_reader_check_package(reader);
	if (status != KHZ_SHEET_OK) return status;
	status = khz_xlsx_reader_first_sheet_part(reader, part_name, sizeof part_name);
	if (status != KHZ_SHEET_OK) return status;
	status = khz_xlsx_reader_shared_strings(reader);
	if (status != KHZ_SHEET_OK) return status;
	return khz_xlsx_reader_parse_sheet(reader, sheet, part_name);
}
