/* khz_xlsx_reader.h - OPC xlsx reader, Ring-0.
 *
 * The counterpart to khz_xlsx.h. That writer emits STORED (uncompressed) zip
 * entries only. This reader accepts both STORED (method 0) and DEFLATE
 * (method 8), so it reads strictly more than this project writes: a workbook
 * saved by Excel is deflated, and since Phase 97 it can be opened here.
 *
 * DEFLATE is handled by khz_inflate.h, which is this project's own decode-only
 * inflater. No zlib, no vendored miniz - the permitted dependency list is
 * still sqlite3 and the standard library, and the decoder allocates its output
 * from the same arena as everything else.
 *
 * Every entry is CRC-32 checked after decompression, against the checksum in
 * the central directory. For a deflated entry that check covers the decoder as
 * much as it covers the archive: if the inflater produced the wrong bytes the
 * CRC will not match, and the load is refused rather than accepted with a
 * plausible-looking wrong value in a cell.
 *
 * Any method other than 0 or 8 is still refused with
 * KHZ_SHEET_ERR_UNSUPPORTED rather than silently mis-parsed.
 *
 * No file I/O happens here. The caller supplies the whole archive as bytes it
 * already holds, which keeps this translation unit free of stdio and leaves
 * the decision of how a file reaches memory - mmap, read, embedded blob - with
 * the caller.
 *
 * Allocation: every buffer, string and entry record comes from the arena the
 * reader was initialised with. The reader takes an arena mark in
 * khz_xlsx_reader_load. If that arena is separate scratch storage,
 * khz_xlsx_reader_reset returns the whole load to the mark. If a worksheet is
 * parsed into a KhzSheet backed by that same arena, reset deliberately returns
 * KHZ_SHEET_ERR_STATE instead: rewinding would invalidate live cell payloads.
 * Use a separate reader arena when reset/reuse after import is required.
 * Nothing is malloc'd.
 */

#ifndef KHZ_XLSX_READER_H
#define KHZ_XLSX_READER_H

#include <stddef.h>
#include <stdint.h>

#include "khz_arena.h"
#include "khz_sheet.h"
#include "khz_status.h"

#ifdef __cplusplus
extern "C" {
#endif

#define KHZ_XLSX_READER_MAX_ENTRIES ((size_t)64)
#define KHZ_XLSX_READER_MAX_STRINGS ((size_t)1 << 20)
#define KHZ_XLSX_READER_MAX_NAME ((size_t)512)

#define KHZ_XLSX_SIG_LOCAL ((uint32_t)0x04034b50u)
#define KHZ_XLSX_SIG_CENTRAL ((uint32_t)0x02014b50u)
#define KHZ_XLSX_SIG_EOCD ((uint32_t)0x06054b50u)

#define KHZ_XLSX_METHOD_STORED ((uint16_t)0)
#define KHZ_XLSX_METHOD_DEFLATE ((uint16_t)8)

typedef struct KhzXlsxEntry {
	const char *name;
	size_t name_len;
	const unsigned char *data;
	size_t size;
	uint32_t declared_crc32;
	uint32_t actual_crc32;
	uint16_t method;
} KhzXlsxEntry;

typedef struct KhzXlsxReadReport {
	uint64_t entries_seen;
	uint64_t entries_stored;
	uint64_t entries_rejected;
	uint64_t crc_failures;
	uint64_t shared_strings;
	uint64_t cells_seen;
	uint64_t cells_loaded;
	uint64_t cells_unsupported;
	uint64_t formulas_seen;
} KhzXlsxReadReport;

typedef struct KhzXlsxReader {
	KhzArena *arena;
	const unsigned char *bytes;
	size_t size;
	KhzXlsxEntry *entries;
	size_t entry_count;
	const char **strings;
	size_t string_count;
	size_t mark;
	KhzXlsxReadReport report;
	int initialised;
	int loaded;
} KhzXlsxReader;

KhzSheetStatus khz_xlsx_reader_init(KhzXlsxReader *reader, KhzArena *arena);
KhzSheetStatus khz_xlsx_reader_load(KhzXlsxReader *reader, const void *bytes, size_t size);
KhzSheetStatus khz_xlsx_reader_find(const KhzXlsxReader *reader, const char *name,
                                    const KhzXlsxEntry **entry);
KhzSheetStatus khz_xlsx_reader_check_package(KhzXlsxReader *reader);
KhzSheetStatus khz_xlsx_reader_shared_strings(KhzXlsxReader *reader);

/* Parses one worksheet into `sheet` through ordinary setters so loaded cells
 * join the normal proof chain. If reader and sheet share the same arena, this
 * call makes the reader's load non-releasable: reset then returns ERR_STATE to
 * protect those live payloads. */
KhzSheetStatus khz_xlsx_reader_parse_sheet(KhzXlsxReader *reader, KhzSheet *sheet,
                                           const char *part_name);

/* Convenience path for the conventional first worksheet. Relationship-based
 * worksheet resolution remains outside this helper's current scope. */
KhzSheetStatus khz_xlsx_reader_read(KhzXlsxReader *reader, KhzSheet *sheet,
                                    const void *bytes, size_t size);

/* Releases reader allocations when the reader arena is independent scratch
 * storage. Returns KHZ_SHEET_ERR_STATE without rewinding when a live imported
 * sheet shares that arena. */
KhzSheetStatus khz_xlsx_reader_reset(KhzXlsxReader *reader);

KhzSheetStatus khz_xlsx_reader_report(const KhzXlsxReader *reader, KhzXlsxReadReport *out);
size_t khz_xlsx_reader_entry_count(const KhzXlsxReader *reader);
KhzSheetStatus khz_xlsx_reader_parse_ref(const char *text, size_t len,
                                         uint32_t *col, uint32_t *row);
uint32_t khz_xlsx_reader_crc32(const unsigned char *data, size_t len);

#ifdef __cplusplus
}
#endif

#endif /* KHZ_XLSX_READER_H */
