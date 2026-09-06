#ifndef KHZ_STATUS_H
#define KHZ_STATUS_H

#ifdef __cplusplus
extern "C" {
#endif

/* One status domain for the sheet layer. The values mirror
   src/KHZ.Sheet.Core/SheetStatus.cs one for one where the meanings overlap, so
   a code crossing the boundary keeps its identity instead of being remapped.

   Codes -1..-6 are the existing managed set. -7 and below are additions the
   managed enum does not yet carry; the C# side must be extended before it can
   claim to name them. */
typedef enum KhzSheetStatus {
    KHZ_SHEET_OK              =   0,
    KHZ_SHEET_ERR_NULL        =  -1,
    KHZ_SHEET_ERR_RANGE       =  -2,
    KHZ_SHEET_ERR_FORMAT      =  -3,
    KHZ_SHEET_ERR_LIMIT       =  -4,
    KHZ_SHEET_ERR_MISSING     =  -5,
    KHZ_SHEET_ERR_UNSUPPORTED =  -6,
    KHZ_SHEET_ERR_OVERFLOW    =  -7,
    KHZ_SHEET_ERR_DIVZERO     =  -8,
    KHZ_SHEET_ERR_STATE       =  -9,
    KHZ_SHEET_ERR_MEMORY      = -10,
    KHZ_SHEET_ERR_CYCLE       = -11,
    KHZ_SHEET_ERR_TYPE        = -12
} KhzSheetStatus;

/* Never returns NULL. An unmapped value yields "ERR_UNKNOWN" rather than a
   bare integer, so a log line is readable without the header at hand. */
const char *khz_sheet_status_name(KhzSheetStatus status);

#ifdef __cplusplus
}
#endif

#endif /* KHZ_STATUS_H */
