#ifndef KHZ_XLSX_H
#define KHZ_XLSX_H

#include <stddef.h>
#include <stdint.h>

#include "khz_arena.h"
#include "khz_sheet.h"
#include "khz_status.h"

#ifdef __cplusplus
extern "C" {
#endif

/* OPC xlsx writer.

   Every part is built in arena memory and the whole package is written in one
   pass. The writer takes an arena mark on entry and releases it on exit, so a
   completed write leaves the arena offset exactly where it found it: writing a
   file does not permanently consume sheet memory.

   Entries are STORED, not deflated. That is a deliberate choice, not a missing
   feature: deflate would mean vendoring a compressor, and the rules permit
   only sqlite3 and the standard library. A stored zip is a valid zip, Excel
   opens it, and the cost is file size rather than correctness. Nothing here
   pretends the files are compressed.

   Timestamps in the zip headers are fixed at 1980-01-01T00:00:00, the earliest
   value the DOS format can express. Two runs over the same sheet therefore
   produce byte-identical output, which is what makes a written file
   diffable and hashable. A real clock would make every write differ. */

#define KHZ_XLSX_MAX_SHEET_NAME ((size_t)31)   /* the format's own ceiling */
#define KHZ_XLSX_MAX_PATH ((size_t)4096)

/* What the write actually did. Reported rather than assumed, because two of
   these numbers are the ones a caller needs in order to distrust the file. */
typedef struct KhzXlsxReport {
    uint64_t cells_written;
    uint64_t shared_strings;
    uint64_t formula_cells;

    /* Cells whose exact value could not survive the format.

       xlsx stores numbers as IEEE-754 doubles. An integer rational is written
       exactly. A non-integer rational such as 1/3 is not representable, so it
       is written as the nearest double and counted here. This is the one place
       in the whole codebase where exactness is lost, and it is lost because
       the file format cannot carry it - not because the arithmetic gave up.
       A caller that needs the exact value must keep the sheet or the ledger.

       lossy_cells == 0 means the file carries every value exactly. */
    uint64_t lossy_cells;

    uint64_t bytes_written;
    uint64_t arena_bytes_used;
} KhzXlsxReport;

/* Writes the sheet as an xlsx package at path.

   sheet_name may be NULL for the default "Sheet1". A name longer than
   KHZ_XLSX_MAX_SHEET_NAME, or containing a character the format forbids
   ( : \ / ? * [ ] ), is refused with KHZ_SHEET_ERR_FORMAT rather than
   silently trimmed - a renamed sheet is a different sheet.

   scratch may be the sheet's own arena or a separate one. All temporary
   memory, including every XML part and the zip directory, comes from it. When
   it cannot supply what the parts need the result is KHZ_SHEET_ERR_MEMORY and
   no file is created; there is no heap fallback and no partial file.

   report is optional.

   Statuses:
     KHZ_SHEET_OK             package written
     KHZ_SHEET_ERR_NULL      sheet, scratch or path was NULL
     KHZ_SHEET_ERR_STATE     sheet not initialised
     KHZ_SHEET_ERR_FORMAT    illegal sheet name
     KHZ_SHEET_ERR_RANGE     path length outside 1..KHZ_XLSX_MAX_PATH
     KHZ_SHEET_ERR_MEMORY    arena exhausted
     KHZ_SHEET_ERR_LIMIT     a part exceeded the size the writer can address
     KHZ_SHEET_ERR_OS        the file could not be created or written */
KhzSheetStatus khz_xlsx_write(const KhzSheet *sheet, KhzArena *scratch,
                              const char *path, const char *sheet_name,
                              KhzXlsxReport *report);

/* Builds the package in memory instead of writing it, for a caller that wants
   to hand the bytes to something else. The buffer is arena owned; take a mark
   before calling if the bytes should be reclaimed afterwards. Note that this
   entry point does not release the arena, since the result lives in it. */
KhzSheetStatus khz_xlsx_build(const KhzSheet *sheet, KhzArena *scratch,
                              const char *sheet_name,
                              const unsigned char **bytes, size_t *length,
                              KhzXlsxReport *report);

/* Column index to A1 letters, 0 based: 0 gives "A", 26 gives "AA", 16383
   gives "XFD". capacity must be at least 4 including the terminator. */
KhzSheetStatus khz_xlsx_column_name(uint32_t col, char *out, size_t capacity,
                                    size_t *written);

#ifdef __cplusplus
}
#endif

#endif /* KHZ_XLSX_H */
