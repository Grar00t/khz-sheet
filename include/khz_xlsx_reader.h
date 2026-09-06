/* khz_xlsx_reader.h - OPC xlsx reader, Ring-0.
 *
 * The counterpart to khz_xlsx.h. That writer emits STORED (uncompressed) zip
 * entries only, and this reader accepts STORED entries only. That is a real
 * limitation, not an oversight: DEFLATE would mean either vendoring an
 * inflater or linking zlib, and zlib is not on the permitted dependency list.
 * A workbook saved by Excel is deflated and will therefore be refused with
 * KHZ_SHEET_ERR_UNSUPPORTED rather than silently mis-parsed. What round-trips
 * today is what this project wrote.
 *
 * No file I/O happens here. The caller supplies the whole archive as bytes it
 * already holds, which keeps this translation unit free of stdio and leaves
 * the decision of how a file reaches memory - mmap, read, embedded blob - with
 * the caller.
 *
 * Allocation: every buffer, string and entry record comes from the arena the
 * reader was initialised with. The reader takes an arena mark in
 * khz_xlsx_reader_load and can hand the whole lot back with
 * khz_xlsx_reader_reset. Nothing is malloc'd.
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

/* An xlsx this project wrote has six parts. The ceiling is generous enough for
 * a third-party writer that adds styles or theme parts, and bounded so a
 * corrupt central directory claiming millions of entries is refused instead of
 * exhausting the arena. */
#define KHZ_XLSX_READER_MAX_ENTRIES ((size_t)64)

/* Upper bound on entries in sharedStrings.xml. */
#define KHZ_XLSX_READER_MAX_STRINGS ((size_t)1 << 20)

/* Longest part name accepted from the central directory. */
#define KHZ_XLSX_READER_MAX_NAME ((size_t)512)

/* Zip signatures, mirrored from the writer. */
#define KHZ_XLSX_SIG_LOCAL ((uint32_t)0x04034b50u)
#define KHZ_XLSX_SIG_CENTRAL ((uint32_t)0x02014b50u)
#define KHZ_XLSX_SIG_EOCD ((uint32_t)0x06054b50u)

/* The only compression method accepted. */
#define KHZ_XLSX_METHOD_STORED ((uint16_t)0)

/* One entry located in the archive. `data` points into the caller's buffer -
 * nothing is copied - and is valid only while that buffer is alive. */
typedef struct KhzXlsxEntry {
	const char *name; /* NUL terminated, arena owned */
	size_t name_len;
	const unsigned char *data; /* into the caller's bytes, not owned */
	size_t size;
	uint32_t declared_crc32; /* as recorded in the central directory */
	uint32_t actual_crc32; /* recomputed over `data` */
	uint16_t method;
} KhzXlsxEntry;

/* Honest counters. Every one is a count of something observed, not a claim
 * that the workbook was correct. */
typedef struct KhzXlsxReadReport {
	uint64_t entries_seen;
	uint64_t entries_stored;
	uint64_t entries_rejected; /* wrong method, or ran past the buffer */
	uint64_t crc_failures;
	uint64_t shared_strings;
	uint64_t cells_seen;
	uint64_t cells_loaded;
	uint64_t cells_unsupported; /* a cell type this reader does not model */
	uint64_t formulas_seen;
} KhzXlsxReadReport;

typedef struct KhzXlsxReader {
	KhzArena *arena;
	const unsigned char *bytes;
	size_t size;
	KhzXlsxEntry *entries;
	size_t entry_count;
	const char **strings; /* shared string table, arena owned */
	size_t string_count;
	size_t mark; /* arena mark taken at load */
	KhzXlsxReadReport report;
	int initialised;
	int loaded;
} KhzXlsxReader;

/* Binds a reader to an arena. Does not read anything. */
KhzSheetStatus khz_xlsx_reader_init(KhzXlsxReader *reader, KhzArena *arena);

/* Parses the end-of-central-directory record and the central directory, then
 * locates and CRC-checks every STORED entry.
 *
 * Returns KHZ_SHEET_ERR_FORMAT when the archive is not a zip or the directory
 * is inconsistent, KHZ_SHEET_ERR_UNSUPPORTED when entries are compressed,
 * KHZ_SHEET_ERR_LIMIT when the directory exceeds the ceilings above, and
 * KHZ_SHEET_ERR_MEMORY when the arena cannot hold the entry table.
 *
 * A CRC mismatch is counted in the report and returns KHZ_SHEET_ERR_FORMAT:
 * a workbook whose stored checksum disagrees with its bytes is refused, not
 * loaded with a warning. */
KhzSheetStatus khz_xlsx_reader_load(KhzXlsxReader *reader, const void *bytes, size_t size);

/* Looks up one part by exact name, e.g. "xl/worksheets/sheet1.xml". Returns
 * KHZ_SHEET_ERR_MISSING when absent. */
KhzSheetStatus khz_xlsx_reader_find(const KhzXlsxReader *reader, const char *name,
                                    const KhzXlsxEntry **entry);

/* Verifies [Content_Types].xml and _rels/.rels are present and name the parts
 * this reader expects. Advisory: it reports structure, it does not rewrite it. */
KhzSheetStatus khz_xlsx_reader_check_package(KhzXlsxReader *reader);

/* Parses xl/sharedStrings.xml into reader->strings. Absent is not an error -
 * a workbook of pure numbers has no such part - and leaves string_count zero. */
KhzSheetStatus khz_xlsx_reader_shared_strings(KhzXlsxReader *reader);

/* Parses one worksheet part into `sheet`, committing each cell through the
 * ordinary khz_sheet_set_* path so every loaded cell joins the proof chain and
 * the commit log exactly as a typed edit would. Loading is an edit, and is
 * recorded as one.
 *
 * Call khz_xlsx_reader_shared_strings first if the sheet uses t="s" cells;
 * without the table those cells are counted as unsupported rather than being
 * given a wrong value. */
KhzSheetStatus khz_xlsx_reader_parse_sheet(KhzXlsxReader *reader, KhzSheet *sheet,
                                           const char *part_name);

/* Convenience: check_package, shared_strings, then parse the first worksheet
 * named by xl/workbook.xml. */
KhzSheetStatus khz_xlsx_reader_read(KhzXlsxReader *reader, KhzSheet *sheet,
                                    const void *bytes, size_t size);

/* Returns the whole load to the arena at the mark taken in load. The reader
 * returns to its initialised state. */
KhzSheetStatus khz_xlsx_reader_reset(KhzXlsxReader *reader);

KhzSheetStatus khz_xlsx_reader_report(const KhzXlsxReader *reader, KhzXlsxReadReport *out);
size_t khz_xlsx_reader_entry_count(const KhzXlsxReader *reader);

/* Parses an A1 reference such as "BC12" into zero-based column and row.
 * Exposed because the sheet parser and the tests both need it. */
KhzSheetStatus khz_xlsx_reader_parse_ref(const char *text, size_t len,
                                         uint32_t *col, uint32_t *row);

/* CRC-32 as zip defines it, same polynomial and same bitwise implementation as
 * the writer. Exposed so a caller can check a buffer without a reader. */
uint32_t khz_xlsx_reader_crc32(const unsigned char *data, size_t len);

#ifdef __cplusplus
}
#endif

#endif /* KHZ_XLSX_READER_H */
