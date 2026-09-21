/* khz_xlsx_reader.c - part 1 of 2: the zip layer.
 *
 * Locates the end-of-central-directory record, walks the central directory,
 * resolves each entry's local header, decompresses it if needed and recomputes
 * its CRC-32. The XML parts are dealt with in src/khz_xlsx_reader_parse.c; the
 * split is by concern, and both translation units are required at link.
 *
 * Every offset read out of the archive is bounds-checked against the buffer
 * before it is used. A zip file is untrusted input: a truncated or hostile
 * central directory that claims an entry lives past the end of the buffer has
 * to be refused, not dereferenced.
 *
 * Phase 97 added DEFLATE. Before it, every method except 0 was refused, which
 * meant no workbook produced by Excel could be opened - Excel always
 * compresses. That made the reader a demonstration rather than a reader. The
 * decompressor is src/khz_inflate.c, written for this project and tested
 * against real zlib output and against the members of a real .xlsx before it
 * was committed.
 */

#include <string.h>

#include "khz_grid.h"
#include "khz_inflate.h"
#include "khz_xlsx_reader.h"

/* Fixed record sizes, excluding the variable-length name/extra/comment. */
#define KHZ_ZIP_EOCD_FIXED ((size_t)22)
#define KHZ_ZIP_CENTRAL_FIXED ((size_t)46)
#define KHZ_ZIP_LOCAL_FIXED ((size_t)30)

/* A zip comment is a 16-bit length, so the EOCD cannot start further back
   than this from the end of the file. */
#define KHZ_ZIP_MAX_COMMENT ((size_t)65535)

/* Method 8. Defined here rather than in the header so that adding DEFLATE
   support did not require an edit to a second file in the same push. */
#define KHZ_ZIP_METHOD_DEFLATE ((uint16_t)8)

/* Largest uncompressed part this reader will produce.

   A compression ratio of a thousand to one is easy to construct, so a small
   archive can declare a part of any size it likes. Without a ceiling the
   arena would absorb the whole thing and report exhaustion - a correct
   outcome reached by the wrong route, and one that would take the rest of
   the workbook down with it. Refusing early names the real problem. */
#define KHZ_XLSX_READER_MAX_PART ((size_t)64 * (size_t)1024 * (size_t)1024)

static uint16_t khz_load_le16(const unsigned char *p)
{
	return (uint16_t)((uint16_t)p[0] | (uint16_t)((uint16_t)p[1] << 8));
}

static uint32_t khz_load_le32(const unsigned char *p)
{
	return (uint32_t)p[0]
	     | ((uint32_t)p[1] << 8)
	     | ((uint32_t)p[2] << 16)
	     | ((uint32_t)p[3] << 24);
}

/* Bitwise, table-free, and deliberately identical to the writer's. A shared
   table would be a static mutable object initialised on first use, which is
   not worth the thread-safety question for a checksum computed once per
   part. */
uint32_t khz_xlsx_reader_crc32(const unsigned char *data, size_t len)
{
	uint32_t crc = 0xffffffffu;
	size_t i;

	if (data == NULL) {
		return 0u;
	}

	for (i = 0; i < len; ++i) {
		unsigned int bit;

		crc ^= (uint32_t)data[i];

		for (bit = 0; bit < 8u; ++bit) {
			uint32_t mask = (uint32_t)0u - (crc & 1u);

			crc = (crc >> 1) ^ (0xedb88320u & mask);
		}
	}

	return crc ^ 0xffffffffu;
}

KhzSheetStatus khz_xlsx_reader_init(KhzXlsxReader *reader, KhzArena *arena)
{
	if (reader == NULL || arena == NULL) {
		return KHZ_SHEET_ERR_NULL;
	}

	memset(reader, 0, sizeof(*reader));
	reader->arena = arena;
	reader->initialised = 1;

	return KHZ_SHEET_OK;
}

size_t khz_xlsx_reader_entry_count(const KhzXlsxReader *reader)
{
	return reader == NULL ? (size_t)0 : reader->entry_count;
}

KhzSheetStatus khz_xlsx_reader_report(const KhzXlsxReader *reader, KhzXlsxReadReport *out)
{
	if (reader == NULL || out == NULL) {
		return KHZ_SHEET_ERR_NULL;
	}

	*out = reader->report;
	return KHZ_SHEET_OK;
}

KhzSheetStatus khz_xlsx_reader_reset(KhzXlsxReader *reader)
{
	KhzArena *arena;
	size_t mark;

	if (reader == NULL) {
		return KHZ_SHEET_ERR_NULL;
	}

	if (reader->initialised == 0) {
		return KHZ_SHEET_ERR_STATE;
	}

	arena = reader->arena;
	mark = reader->mark;

	if (reader->loaded != 0 && arena != NULL) {
		if (khz_arena_release(arena, mark) != KHZ_ARENA_OK) {
			return KHZ_SHEET_ERR_STATE;
		}
	}

	memset(reader, 0, sizeof(*reader));
	reader->arena = arena;
	reader->initialised = 1;

	return KHZ_SHEET_OK;
}

/* Scans backwards for the EOCD signature. Backwards because the record sits at
   the end but at an unknown distance from it: the trailing comment is
   variable. The first match found scanning back is the real one for any
   archive this project writes, which emits no comment at all. */
static KhzSheetStatus khz_zip_find_eocd(const unsigned char *bytes, size_t size, size_t *offset)
{
	size_t limit;
	size_t back;

	if (size < KHZ_ZIP_EOCD_FIXED) {
		return KHZ_SHEET_ERR_FORMAT;
	}

	limit = size - KHZ_ZIP_EOCD_FIXED;

	if (limit > KHZ_ZIP_MAX_COMMENT) {
		limit = KHZ_ZIP_MAX_COMMENT;
	}

	for (back = 0; back <= limit; ++back) {
		size_t at = size - KHZ_ZIP_EOCD_FIXED - back;

		if (khz_load_le32(bytes + at) == KHZ_XLSX_SIG_EOCD) {
			*offset = at;
			return KHZ_SHEET_OK;
		}
	}

	return KHZ_SHEET_ERR_FORMAT;
}

/* Confirms the local header agrees with the central directory and returns the
   offset of the payload. The two records duplicate the method and the CRC, and
   a disagreement between them means the archive was rewritten badly, so it is
   treated as a format error rather than trusting either copy.

   payload_bytes is the size on disk - the compressed size, which for a stored
   entry is also the uncompressed size. Passing the uncompressed size here for
   a DEFLATE entry would check the wrong extent, and would pass for a part
   that compressed well while failing for one that did not. The compressed
   size is taken from the central directory rather than the local header
   because an entry written with a data descriptor carries zeroes here. */
static KhzSheetStatus khz_zip_payload(const unsigned char *bytes, size_t size,
                                      size_t local_offset, uint16_t method,
                                      size_t payload_bytes, size_t *data_offset)
{
	size_t name_len;
	size_t extra_len;
	size_t start;

	if (local_offset > size || size - local_offset < KHZ_ZIP_LOCAL_FIXED) {
		return KHZ_SHEET_ERR_FORMAT;
	}

	if (khz_load_le32(bytes + local_offset) != KHZ_XLSX_SIG_LOCAL) {
		return KHZ_SHEET_ERR_FORMAT;
	}

	if (khz_load_le16(bytes + local_offset + 8) != method) {
		return KHZ_SHEET_ERR_FORMAT;
	}

	name_len = (size_t)khz_load_le16(bytes + local_offset + 26);
	extra_len = (size_t)khz_load_le16(bytes + local_offset + 28);

	start = local_offset + KHZ_ZIP_LOCAL_FIXED;

	if (name_len > size - start) {
		return KHZ_SHEET_ERR_FORMAT;
	}

	start += name_len;

	if (extra_len > size - start) {
		return KHZ_SHEET_ERR_FORMAT;
	}

	start += extra_len;

	if (payload_bytes > size - start) {
		return KHZ_SHEET_ERR_FORMAT;
	}

	*data_offset = start;
	return KHZ_SHEET_OK;
}

KhzSheetStatus khz_xlsx_reader_load(KhzXlsxReader *reader, const void *bytes, size_t size)
{
	const unsigned char *raw;
	size_t eocd;
	size_t cd_offset;
	size_t cd_size;
	size_t cd_end;
	size_t total;
	size_t cursor;
	size_t index;
	size_t mark;
	KhzXlsxEntry *table;
	KhzSheetStatus status;

	if (reader == NULL || bytes == NULL) {
		return KHZ_SHEET_ERR_NULL;
	}

	if (reader->initialised == 0 || reader->arena == NULL) {
		return KHZ_SHEET_ERR_STATE;
	}

	if (reader->loaded != 0) {
		return KHZ_SHEET_ERR_STATE;
	}

	raw = (const unsigned char *)bytes;

	status = khz_zip_find_eocd(raw, size, &eocd);

	if (status != KHZ_SHEET_OK) {
		return status;
	}

	total = (size_t)khz_load_le16(raw + eocd + 10);
	cd_size = (size_t)khz_load_le32(raw + eocd + 12);
	cd_offset = (size_t)khz_load_le32(raw + eocd + 16);

	if (total == 0) {
		return KHZ_SHEET_ERR_FORMAT;
	}

	if (total > KHZ_XLSX_READER_MAX_ENTRIES) {
		return KHZ_SHEET_ERR_LIMIT;
	}

	if (cd_offset > size || cd_size > size - cd_offset) {
		return KHZ_SHEET_ERR_FORMAT;
	}
	cd_end = cd_offset + cd_size;
	if (cd_end > eocd) {
		return KHZ_SHEET_ERR_FORMAT;
	}

	/* One mark for the whole load. Either every entry record, name and
	   inflated payload is allocated, or the arena is returned to exactly
	   where it was. */
	mark = khz_arena_mark(reader->arena);

	table = (KhzXlsxEntry *)khz_arena_alloc_zeroed(reader->arena,
	                                               total * sizeof(KhzXlsxEntry));

	if (table == NULL) {
		(void)khz_arena_release(reader->arena, mark);
		return KHZ_SHEET_ERR_MEMORY;
	}

	cursor = cd_offset;

	for (index = 0; index < total; ++index) {
		size_t name_len;
		size_t extra_len;
		size_t comment_len;
		size_t local_offset;
		size_t declared_size;
		size_t packed_size;
		size_t data_offset;
		size_t record_end;
		uint16_t method;
		uint32_t declared_crc;
		const unsigned char *payload;
		char *name;

		if (cursor > cd_end || cd_end - cursor < KHZ_ZIP_CENTRAL_FIXED) {
			(void)khz_arena_release(reader->arena, mark);
			return KHZ_SHEET_ERR_FORMAT;
		}

		if (khz_load_le32(raw + cursor) != KHZ_XLSX_SIG_CENTRAL) {
			(void)khz_arena_release(reader->arena, mark);
			return KHZ_SHEET_ERR_FORMAT;
		}

		method = khz_load_le16(raw + cursor + 10);
		declared_crc = khz_load_le32(raw + cursor + 16);
		packed_size = (size_t)khz_load_le32(raw + cursor + 20);
		declared_size = (size_t)khz_load_le32(raw + cursor + 24);
		name_len = (size_t)khz_load_le16(raw + cursor + 28);
		extra_len = (size_t)khz_load_le16(raw + cursor + 30);
		comment_len = (size_t)khz_load_le16(raw + cursor + 32);
		local_offset = (size_t)khz_load_le32(raw + cursor + 42);

		reader->report.entries_seen += 1u;

		if (name_len == 0 || name_len > KHZ_XLSX_READER_MAX_NAME) {
			(void)khz_arena_release(reader->arena, mark);
			return KHZ_SHEET_ERR_LIMIT;
		}

		record_end = cursor + KHZ_ZIP_CENTRAL_FIXED;
		if (name_len > cd_end - record_end) {
			(void)khz_arena_release(reader->arena, mark);
			return KHZ_SHEET_ERR_FORMAT;
		}
		record_end += name_len;
		if (extra_len > cd_end - record_end) {
			(void)khz_arena_release(reader->arena, mark);
			return KHZ_SHEET_ERR_FORMAT;
		}
		record_end += extra_len;
		if (comment_len > cd_end - record_end) {
			(void)khz_arena_release(reader->arena, mark);
			return KHZ_SHEET_ERR_FORMAT;
		}
		record_end += comment_len;

		/* Stored and DEFLATE only. Every other method in the appnote -
		   bzip2, LZMA, zstd, the obsolete shrink and implode - is refused
		   by name rather than attempted. Excel writes 8, and this project
		   writes 0. */
		if (method != KHZ_XLSX_METHOD_STORED
		    && method != KHZ_ZIP_METHOD_DEFLATE) {
			reader->report.entries_rejected += 1u;
			(void)khz_arena_release(reader->arena, mark);
			return KHZ_SHEET_ERR_UNSUPPORTED;
		}

		if (declared_size > KHZ_XLSX_READER_MAX_PART) {
			reader->report.entries_rejected += 1u;
			(void)khz_arena_release(reader->arena, mark);
			return KHZ_SHEET_ERR_LIMIT;
		}

		/* A stored entry that claims two different sizes is describing
		   something impossible. */
		if (method == KHZ_XLSX_METHOD_STORED && packed_size != declared_size) {
			reader->report.entries_rejected += 1u;
			(void)khz_arena_release(reader->arena, mark);
			return KHZ_SHEET_ERR_FORMAT;
		}

		status = khz_zip_payload(raw, size, local_offset, method,
		                         packed_size, &data_offset);

		if (status != KHZ_SHEET_OK) {
			reader->report.entries_rejected += 1u;
			(void)khz_arena_release(reader->arena, mark);
			return status;
		}

		if (method == KHZ_XLSX_METHOD_STORED) {
			/* Points straight into the caller's buffer. No copy: the
			   bytes are already exactly the part. */
			payload = raw + data_offset;
			reader->report.entries_stored += 1u;
		} else {
			unsigned char *inflated = NULL;
			size_t produced = 0;

			status = khz_inflate_to_arena(reader->arena,
			                              raw + data_offset,
			                              packed_size,
			                              declared_size,
			                              &inflated,
			                              &produced);

			if (status != KHZ_SHEET_OK) {
				/* Includes the case where the stream decoded cleanly
				   but produced a different number of bytes than the
				   header declared, which khz_inflate_to_arena reports
				   as ERR_FORMAT. */
				reader->report.entries_rejected += 1u;
				(void)khz_arena_release(reader->arena, mark);
				return status;
			}

			payload = inflated;

			/* entries_stored counts entries whose payload was made
			   available, which now includes inflated ones. There is no
			   separate counter for the compressed case because adding
			   one means editing khz_xlsx_reader.h, and this push does
			   not touch the header. Worth a field in a later phase. */
			reader->report.entries_stored += 1u;
		}

		name = (char *)khz_arena_alloc_zeroed(reader->arena, name_len + 1u);

		if (name == NULL) {
			(void)khz_arena_release(reader->arena, mark);
			return KHZ_SHEET_ERR_MEMORY;
		}

		memcpy(name, raw + cursor + KHZ_ZIP_CENTRAL_FIXED, name_len);
		name[name_len] = '\0';

		table[index].name = name;
		table[index].name_len = name_len;
		table[index].data = payload;
		table[index].size = declared_size;
		table[index].method = method;
		table[index].declared_crc32 = declared_crc;

		/* Always over the uncompressed bytes, whichever path produced
		   them. For a DEFLATE entry this checks the decoder as much as it
		   checks the archive: a decoder bug that produced the right byte
		   count and the wrong contents would be caught here. */
		table[index].actual_crc32 =
			khz_xlsx_reader_crc32(payload, declared_size);

		if (table[index].actual_crc32 != declared_crc) {
			reader->report.crc_failures += 1u;
			(void)khz_arena_release(reader->arena, mark);
			return KHZ_SHEET_ERR_FORMAT;
		}

		cursor = record_end;
	}

	if (cursor != cd_end) {
		(void)khz_arena_release(reader->arena, mark);
		return KHZ_SHEET_ERR_FORMAT;
	}

	reader->bytes = raw;
	reader->size = size;
	reader->entries = table;
	reader->entry_count = total;
	reader->mark = mark;
	reader->loaded = 1;

	return KHZ_SHEET_OK;
}

KhzSheetStatus khz_xlsx_reader_find(const KhzXlsxReader *reader, const char *name,
                                    const KhzXlsxEntry **entry)
{
	size_t index;

	if (reader == NULL || name == NULL || entry == NULL) {
		return KHZ_SHEET_ERR_NULL;
	}

	*entry = NULL;

	if (reader->loaded == 0) {
		return KHZ_SHEET_ERR_STATE;
	}

	for (index = 0; index < reader->entry_count; ++index) {
		if (strcmp(reader->entries[index].name, name) == 0) {
			*entry = &reader->entries[index];
			return KHZ_SHEET_OK;
		}
	}

	return KHZ_SHEET_ERR_MISSING;
}

/* "BC12" -> col 54, row 11. Letters are the column in base 26 with A as one,
   digits are the one-based row. Both are converted to the zero-based pair the
   grid uses, and both are range-checked: a reference past the grid's limits is
   refused here rather than becoming a rejected upsert later. */
KhzSheetStatus khz_xlsx_reader_parse_ref(const char *text, size_t len,
                                         uint32_t *col, uint32_t *row)
{
	size_t at = 0;
	uint64_t column = 0;
	uint64_t line = 0;
	int saw_letter = 0;
	int saw_digit = 0;

	if (text == NULL || col == NULL || row == NULL) {
		return KHZ_SHEET_ERR_NULL;
	}

	/* An absolute reference is the same cell; the marker carries no meaning
	   once the formula has been lowered, so it is skipped rather than
	   refused. */
	if (at < len && text[at] == '$') {
		at += 1u;
	}

	while (at < len && text[at] >= 'A' && text[at] <= 'Z') {
		column = column * 26u + (uint64_t)(text[at] - 'A') + 1u;
		saw_letter = 1;
		at += 1u;

		if (column > (uint64_t)KHZ_GRID_MAX_COLUMNS) {
			return KHZ_SHEET_ERR_RANGE;
		}
	}

	if (at < len && text[at] == '$') {
		at += 1u;
	}

	while (at < len && text[at] >= '0' && text[at] <= '9') {
		line = line * 10u + (uint64_t)(text[at] - '0');
		saw_digit = 1;
		at += 1u;

		if (line > (uint64_t)KHZ_GRID_MAX_ROWS) {
			return KHZ_SHEET_ERR_RANGE;
		}
	}

	if (saw_letter == 0 || saw_digit == 0 || at != len) {
		return KHZ_SHEET_ERR_FORMAT;
	}

	if (column == 0 || line == 0) {
		return KHZ_SHEET_ERR_FORMAT;
	}

	*col = (uint32_t)(column - 1u);
	*row = (uint32_t)(line - 1u);

	return KHZ_SHEET_OK;
}
