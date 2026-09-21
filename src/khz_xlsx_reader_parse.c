/* khz_xlsx_reader_parse.c - part 2 of 2: the XML layer.
 *
 * Turns the parts located by khz_xlsx_reader.c into cells. Pairs with that
 * file; both are required at link.
 *
 * This is a scanner, not an XML parser. It looks for the specific elements the
 * SpreadsheetML worksheet grammar puts cells in and ignores everything else.
 * That is the right trade for a reader whose stated scope is what this project
 * writes, and it is stated plainly rather than dressed up: there is no DTD
 * handling, no namespace resolution, no CDATA, and no attribute normalisation.
 * A part that does not match the shapes below yields counted, unsupported
 * cells rather than wrong values.
 *
 * Every buffer comes from the reader's arena, inside the mark taken at load,
 * so khz_xlsx_reader_reset gives all of it back.
 */

#include <string.h>

#include "khz_xlsx_reader.h"

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
static int khz_xml_attr(const char *tag, size_t tag_len, const char *name,
                        const char **value, size_t *value_len)
{
	size_t name_len = strlen(name);
	size_t at;

	*value = NULL;
	*value_len = 0;

	for (at = 0; at + name_len + 2u <= tag_len; ++at) {
		size_t start;
		size_t end;

		if (memcmp(tag + at, name, name_len) != 0) {
			continue;
		}

		/* Must be a whole attribute name: preceded by whitespace and
		   followed by ={quote}. Without the left-hand check, "t" would match
		   inside "count". */
		if (at == 0 || (tag[at - 1] != ' ' && tag[at - 1] != '\t')) {
			continue;
		}

		if (tag[at + name_len] != '=' || tag[at + name_len + 1] != '"') {
			continue;
		}

		start = at + name_len + 2u;

		for (end = start; end < tag_len; ++end) {
			if (tag[end] == '"') {
				*value = tag + start;
				*value_len = end - start;
				return 1;
			}
		}

		return 0;
	}

	return 0;
}

/* Expands the five predefined entities into an arena buffer. Numeric character
   references are not expanded: they are left as written and counted by the
   caller as text, because silently mangling them would be worse than passing
   them through visibly. */
static KhzSheetStatus khz_xml_decode(KhzArena *arena, const char *src, size_t len,
                                     char **out, size_t *out_len)
{
	char *buffer;
	size_t read_at = 0;
	size_t write_at = 0;

	if (arena == NULL || src == NULL || out == NULL || out_len == NULL) {
		return KHZ_SHEET_ERR_NULL;
	}

	if (len > KHZ_XLSX_MAX_TEXT) {
		return KHZ_SHEET_ERR_LIMIT;
	}

	buffer = (char *)khz_arena_alloc_zeroed(arena, len + 1u);

	if (buffer == NULL) {
		return KHZ_SHEET_ERR_MEMORY;
	}

	while (read_at < len) {
		size_t left = len - read_at;

		if (src[read_at] == '&') {
			if (left >= 5u && memcmp(src + read_at, "&amp;", 5u) == 0) {
				buffer[write_at++] = '&';
				read_at += 5u;
				continue;
			}

			if (left >= 4u && memcmp(src + read_at, "&lt;", 4u) == 0) {
				buffer[write_at++] = '<';
				read_at += 4u;
				continue;
			}

			if (left >= 4u && memcmp(src + read_at, "&gt;", 4u) == 0) {
				buffer[write_at++] = '>';
				read_at += 4u;
				continue;
			}

			if (left >= 6u && memcmp(src + read_at, "&quot;", 6u) == 0) {
				buffer[write_at++] = '"';
				read_at += 6u;
				continue;
			}

			if (left >= 6u && memcmp(src + read_at, "&apos;", 6u) == 0) {
				buffer[write_at++] = '\'';
				read_at += 6u;
				continue;
			}
		}

		buffer[write_at++] = src[read_at++];
	}

	buffer[write_at] = '\0';
	*out = buffer;
	*out_len = write_at;

	return KHZ_SHEET_OK;
}

static KhzSheetStatus khz_xlsx_apply_exponent(KhzRational value,
                                               int exponent_negative,
                                               uint32_t exponent,
                                               KhzRational *out)
{
	KhzRational factor;
	KhzSheetStatus status;

	if (out == NULL) {
		return KHZ_SHEET_ERR_NULL;
	}

	if (value.num == (int64_t)0 || exponent == (uint32_t)0) {
		*out = value;
		return KHZ_SHEET_OK;
	}

	factor.num = exponent_negative != 0 ? (int64_t)1 : (int64_t)10;
	factor.den = exponent_negative != 0 ? (int64_t)10 : (int64_t)1;

	while (exponent != (uint32_t)0) {
		status = khz_rational_mul(value, factor, &value);
		if (status != KHZ_SHEET_OK) {
			return status;
		}
		exponent -= (uint32_t)1;
	}

	*out = value;
	return KHZ_SHEET_OK;
}

/* Decimal/exponent text to an exact rational. "3.25" becomes 13/4 and
   "1e-8" becomes 1/100000000. No floating conversion is involved. If the
   exact expanded value cannot fit the rational contract, overflow is returned
   rather than rounding to a nearby value. */
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

	if (text == NULL || out == NULL) {
		return KHZ_SHEET_ERR_NULL;
	}

	if (len == 0) {
		return KHZ_SHEET_ERR_FORMAT;
	}

	if (text[at] == '-') {
		negative = 1;
		at += 1u;
	} else if (text[at] == '+') {
		at += 1u;
	}

	for (; at < len; ++at) {
		char ch = text[at];

		if (ch == 'e' || ch == 'E') {
			seen_exponent = 1;
			at += 1u;
			break;
		}

		if (ch == '.') {
			if (seen_point != 0) {
				return KHZ_SHEET_ERR_FORMAT;
			}
			seen_point = 1;
			continue;
		}

		if (ch < '0' || ch > '9') {
			return KHZ_SHEET_ERR_FORMAT;
		}

		if (mantissa > (INT64_MAX - 9) / 10) {
			return KHZ_SHEET_ERR_OVERFLOW;
		}

		mantissa = mantissa * 10 + (int64_t)(ch - '0');
		seen_digit = 1;

		if (seen_point != 0) {
			if (scale > KHZ_XLSX_MAX_SCALE / 10) {
				return KHZ_SHEET_ERR_OVERFLOW;
			}
			scale *= 10;
		}
	}

	if (seen_digit == 0) {
		return KHZ_SHEET_ERR_FORMAT;
	}

	if (seen_exponent != 0) {
		if (at >= len) {
			return KHZ_SHEET_ERR_FORMAT;
		}
		if (text[at] == '-' || text[at] == '+') {
			exponent_negative = text[at] == '-' ? 1 : 0;
			at += 1u;
		}
		if (at >= len) {
			return KHZ_SHEET_ERR_FORMAT;
		}

		for (; at < len; ++at) {
			char ch = text[at];
			uint32_t digit;

			if (ch < '0' || ch > '9') {
				return KHZ_SHEET_ERR_FORMAT;
			}
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

		if (exponent_digit == 0) {
			return KHZ_SHEET_ERR_FORMAT;
		}
	}

	if (negative != 0) {
		mantissa = -mantissa;
	}

	status = khz_rational_make(mantissa, scale, &value);
	if (status != KHZ_SHEET_OK) {
		return status;
	}

	/* Zero is unchanged by any finite decimal exponent. We still parsed every
	   exponent character above so malformed zero literals are not accepted. */
	if (value.num == (int64_t)0) {
		*out = value;
		return KHZ_SHEET_OK;
	}

	if (exponent_too_large != 0) {
		return KHZ_SHEET_ERR_OVERFLOW;
	}

	return khz_xlsx_apply_exponent(value, exponent_negative, exponent, out);
}

KhzSheetStatus khz_xlsx_reader_check_package(KhzXlsxReader *reader)
{
	const KhzXlsxEntry *entry;
	KhzSheetStatus status;

	if (reader == NULL) {
		return KHZ_SHEET_ERR_NULL;
	}

	if (reader->loaded == 0) {
		return KHZ_SHEET_ERR_STATE;
	}

	status = khz_xlsx_reader_find(reader, "[Content_Types].xml", &entry);

	if (status != KHZ_SHEET_OK) {
		return status;
	}

	status = khz_xlsx_reader_find(reader, "_rels/.rels", &entry);

	if (status != KHZ_SHEET_OK) {
		return status;
	}

	status = khz_xlsx_reader_find(reader, "xl/workbook.xml", &entry);

	if (status != KHZ_SHEET_OK) {
		return status;
	}

	/* A workbook with no <sheet> element has no worksheet to load, which is a
	   format error rather than an empty success. */
	if (khz_xml_find((const char *)entry->data, entry->size, "<sheet ") == NULL) {
		return KHZ_SHEET_ERR_FORMAT;
	}

	return KHZ_SHEET_OK;
}

KhzSheetStatus khz_xlsx_reader_shared_strings(KhzXlsxReader *reader)
{
	const KhzXlsxEntry *entry;
	const char *part;
	const char *cursor;
	const char *limit;
	const char **table;
	size_t counted = 0;
	size_t index = 0;
	KhzSheetStatus status;

	if (reader == NULL) {
		return KHZ_SHEET_ERR_NULL;
	}

	if (reader->loaded == 0) {
		return KHZ_SHEET_ERR_STATE;
	}

	status = khz_xlsx_reader_find(reader, "xl/sharedStrings.xml", &entry);

	/* Absent is legitimate: a workbook of pure numbers has no such part. */
	if (status == KHZ_SHEET_ERR_MISSING) {
		reader->strings = NULL;
		reader->string_count = 0;
		return KHZ_SHEET_OK;
	}

	if (status != KHZ_SHEET_OK) {
		return status;
	}

	part = (const char *)entry->data;
	limit = part + entry->size;

	/* Counted first, then filled. Two passes over a part already in memory is
	   cheaper than growing an array in a bump allocator that cannot free. */
	cursor = part;

	while (cursor < limit) {
		const char *found = khz_xml_find(cursor, (size_t)(limit - cursor), "<si>");

		if (found == NULL) {
			break;
		}

		counted += 1u;
		cursor = found + 4;
	}

	if (counted == 0) {
		reader->strings = NULL;
		reader->string_count = 0;
		return KHZ_SHEET_OK;
	}

	if (counted > KHZ_XLSX_READER_MAX_STRINGS) {
		return KHZ_SHEET_ERR_LIMIT;
	}

	table = (const char **)khz_arena_alloc_zeroed(reader->arena,
	                                              counted * sizeof(const char *));

	if (table == NULL) {
		return KHZ_SHEET_ERR_MEMORY;
	}

	cursor = part;

	while (index < counted && cursor < limit) {
		const char *item = khz_xml_find(cursor, (size_t)(limit - cursor), "<si>");
		const char *item_end;
		const char *open;
		const char *close;
		char *decoded;
		size_t decoded_len;

		if (item == NULL) {
			break;
		}

		item_end = khz_xml_find(item, (size_t)(limit - item), "</si>");

		if (item_end == NULL) {
			return KHZ_SHEET_ERR_FORMAT;
		}

		/* Only the first <t> run of each <si> is taken. A shared string split
		   into several formatted runs therefore loads as its first run, which
		   is a real limitation and is what cells_unsupported cannot express
		   because the cell does load - just not with all of its text. */
		open = khz_xml_find(item, (size_t)(item_end - item), "<t");

		if (open == NULL) {
			table[index] = "";
			index += 1u;
			cursor = item_end + 5;
			continue;
		}

		open = khz_xml_find(open, (size_t)(item_end - open), ">");

		if (open == NULL) {
			return KHZ_SHEET_ERR_FORMAT;
		}

		open += 1;
		close = khz_xml_find(open, (size_t)(item_end - open), "</t>");

		if (close == NULL) {
			return KHZ_SHEET_ERR_FORMAT;
		}

		status = khz_xml_decode(reader->arena, open, (size_t)(close - open),
		                        &decoded, &decoded_len);

		if (status != KHZ_SHEET_OK) {
			return status;
		}

		table[index] = decoded;
		index += 1u;
		cursor = item_end + 5;
	}

	reader->strings = table;
	reader->string_count = index;
	reader->report.shared_strings = (uint64_t)index;

	return KHZ_SHEET_OK;
}

KhzSheetStatus khz_xlsx_reader_parse_sheet(KhzXlsxReader *reader, KhzSheet *sheet,
                                           const char *part_name)
{
	const KhzXlsxEntry *entry;
	const char *cursor;
	const char *limit;
	KhzSheetStatus status;

	if (reader == NULL || sheet == NULL || part_name == NULL) {
		return KHZ_SHEET_ERR_NULL;
	}

	if (reader->loaded == 0) {
		return KHZ_SHEET_ERR_STATE;
	}

	status = khz_xlsx_reader_find(reader, part_name, &entry);

	if (status != KHZ_SHEET_OK) {
		return status;
	}

	cursor = (const char *)entry->data;
	limit = cursor + entry->size;

	while (cursor < limit) {
		const char *open = khz_xml_find(cursor, (size_t)(limit - cursor), "<c ");
		const char *tag_end;
		const char *body_end;
		const char *ref;
		const char *type;
		const char *value_open;
		const char *value_close;
		const char *formula_open;
		size_t tag_len;
		size_t ref_len;
		size_t type_len;
		uint32_t col = 0;
		uint32_t row = 0;
		int self_closing;

		if (open == NULL) {
			break;
		}

		tag_end = khz_xml_find(open, (size_t)(limit - open), ">");

		if (tag_end == NULL) {
			return KHZ_SHEET_ERR_FORMAT;
		}

		tag_len = (size_t)(tag_end - open);
		self_closing = (tag_len > 0 && *(tag_end - 1) == '/') ? 1 : 0;

		reader->report.cells_seen += 1u;

		if (khz_xml_attr(open, tag_len, "r", &ref, &ref_len) == 0) {
			return KHZ_SHEET_ERR_FORMAT;
		}

		status = khz_xlsx_reader_parse_ref(ref, ref_len, &col, &row);

		if (status != KHZ_SHEET_OK) {
			return status;
		}

		/* <c r="A1"/> is a styled but empty cell. Nothing is written: an
		   empty cell is the grid's default, and committing one would put a
		   link on the proof chain for a cell that holds nothing. */
		if (self_closing != 0) {
			cursor = tag_end + 1;
			continue;
		}

		body_end = khz_xml_find(tag_end, (size_t)(limit - tag_end), "</c>");

		if (body_end == NULL) {
			return KHZ_SHEET_ERR_FORMAT;
		}

		if (khz_xml_attr(open, tag_len, "t", &type, &type_len) == 0) {
			type = NULL;
			type_len = 0;
		}

		formula_open = khz_xml_find(tag_end, (size_t)(body_end - tag_end), "<f>");

		/* A formula cell is loaded as its formula, and the cached <v> Excel
		   wrote beside it is discarded rather than trusted. The formula is
		   the authority; a cached result this engine did not compute has no
		   place on the proof chain. It will be recalculated. */
		if (formula_open != NULL) {
			const char *formula_close =
				khz_xml_find(formula_open, (size_t)(body_end - formula_open), "</f>");
			char *decoded;
			size_t decoded_len;

			reader->report.formulas_seen += 1u;

			if (formula_close == NULL) {
				return KHZ_SHEET_ERR_FORMAT;
			}

			status = khz_xml_decode(reader->arena, formula_open + 3,
			                        (size_t)(formula_close - (formula_open + 3)),
			                        &decoded, &decoded_len);

			if (status != KHZ_SHEET_OK) {
				return status;
			}

			status = khz_sheet_set_formula(sheet, col, row, decoded, decoded_len);

			if (status != KHZ_SHEET_OK) {
				return status;
			}

			reader->report.cells_loaded += 1u;
			cursor = body_end + 4;
			continue;
		}

		value_open = khz_xml_find(tag_end, (size_t)(body_end - tag_end), "<v>");

		if (value_open == NULL) {
			reader->report.cells_unsupported += 1u;
			cursor = body_end + 4;
			continue;
		}

		value_open += 3;
		value_close = khz_xml_find(value_open, (size_t)(body_end - value_open), "</v>");

		if (value_close == NULL) {
			return KHZ_SHEET_ERR_FORMAT;
		}

		if (type != NULL && type_len == 1u && type[0] == 's') {
			/* Shared string: <v> holds an index into sharedStrings.xml. */
			KhzRational slot;
			int64_t which;

			status = khz_xlsx_parse_number(value_open,
			                               (size_t)(value_close - value_open), &slot);

			if (status != KHZ_SHEET_OK) {
				return status;
			}

			status = khz_rational_to_i64(slot, &which);

			if (status != KHZ_SHEET_OK) {
				return KHZ_SHEET_ERR_FORMAT;
			}

			/* Without the table, or with an index outside it, the cell is
			   counted as unsupported. It is not given a placeholder: a wrong
			   string is worse than a missing one. */
			if (which < 0 || reader->strings == NULL
			    || (size_t)which >= reader->string_count) {
				reader->report.cells_unsupported += 1u;
				cursor = body_end + 4;
				continue;
			}

			status = khz_sheet_set_text(sheet, col, row,
			                            reader->strings[(size_t)which],
			                            strlen(reader->strings[(size_t)which]));
		} else if (type != NULL && type_len == 3u && memcmp(type, "str", 3u) == 0) {
			char *decoded;
			size_t decoded_len;

			status = khz_xml_decode(reader->arena, value_open,
			                        (size_t)(value_close - value_open),
			                        &decoded, &decoded_len);

			if (status != KHZ_SHEET_OK) {
				return status;
			}

			status = khz_sheet_set_text(sheet, col, row, decoded, decoded_len);
		} else if (type != NULL && type_len == 1u && type[0] == 'b') {
			int flag = (value_close > value_open && *value_open == '1') ? 1 : 0;

			status = khz_sheet_set_bool(sheet, col, row, flag);
		} else if (type != NULL && type_len == 1u && type[0] == 'e') {
			/* An error cell is a cached result of a computation this engine did
			   not perform. Counted, not loaded. */
			reader->report.cells_unsupported += 1u;
			cursor = body_end + 4;
			continue;
		} else if (type != NULL && type_len == 9u
		           && memcmp(type, "inlineStr", 9u) == 0) {
			/* Inline strings live in <is><t>, not <v>. Reaching here means the
			   part declared one and wrote a <v> anyway. Refused. */
			reader->report.cells_unsupported += 1u;
			cursor = body_end + 4;
			continue;
		} else {
			/* No t attribute, or t="n": a number. */
			KhzRational value;

			status = khz_xlsx_parse_number(value_open,
			                               (size_t)(value_close - value_open), &value);

			if (status != KHZ_SHEET_OK) {
				return status;
			}

			status = khz_sheet_set_rational(sheet, col, row, value);
		}

		if (status != KHZ_SHEET_OK) {
			return status;
		}

		reader->report.cells_loaded += 1u;
		cursor = body_end + 4;
	}

	return KHZ_SHEET_OK;
}

KhzSheetStatus khz_xlsx_reader_read(KhzXlsxReader *reader, KhzSheet *sheet,
                                    const void *bytes, size_t size)
{
	KhzSheetStatus status;

	if (reader == NULL || sheet == NULL || bytes == NULL) {
		return KHZ_SHEET_ERR_NULL;
	}

	status = khz_xlsx_reader_load(reader, bytes, size);

	if (status != KHZ_SHEET_OK) {
		return status;
	}

	status = khz_xlsx_reader_check_package(reader);

	if (status != KHZ_SHEET_OK) {
		return status;
	}

	status = khz_xlsx_reader_shared_strings(reader);

	if (status != KHZ_SHEET_OK) {
		return status;
	}

	/* The worksheet path is taken by convention, not by resolving the
	   workbook's relationship part. Correct for everything this project
	   writes and for the common single-sheet case; a workbook that names its
	   worksheet anything else must be parsed with khz_xlsx_reader_parse_sheet
	   and an explicit part name. Walking xl/_rels/workbook.xml.rels to map
	   r:id to a target is the honest fix and is not done here. */
	return khz_xlsx_reader_parse_sheet(reader, sheet, "xl/worksheets/sheet1.xml");
}
