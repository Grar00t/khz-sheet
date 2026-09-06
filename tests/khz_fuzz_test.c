/* khz_fuzz_test.c - the reader must not read out of bounds on broken input.
 *
 * khz_xlsx_test.c proves the happy path: a correct archive becomes correct
 * cells. This proves something different and, for a parser, more important -
 * that malformed input produces a status code instead of a memory error.
 *
 * The distinction matters because of what the existing evidence actually
 * covers. Every byte khz_inflate.c has ever decoded was produced by zlib and
 * was well formed. Its 19 known-answer vectors are one encoder's correct
 * output. None of that exercises a truncated stream, a length that runs past
 * the end of the buffer, a distance code pointing before the start of the
 * window, or a central directory claiming an entry larger than the file. Those
 * are the inputs a reader meets in practice, and they are where parsers fail.
 *
 * Method: take the good fixture and break it 800 ways.
 *
 *   - truncation at every eighth byte, from empty to one byte short
 *   - single-byte corruption at pseudorandom offsets, XOR 0xFF
 *
 * The offsets come from a fixed-seed generator, so a failure reproduces
 * exactly. This is not a substitute for a real fuzzer with coverage feedback -
 * it will not discover deep states - but it costs a few hundredths of a second
 * per run and covers the shallow cases that random corruption actually hits.
 *
 * What is NOT checked: the values loaded. A corrupted archive whose CRC-32
 * happens to match is entitled to produce nonsense cells, and asserting
 * anything about them would be asserting a behaviour the format does not
 * promise. Any status is acceptable, including KHZ_SHEET_OK.
 *
 * What IS checked, besides survival: if a read reports OK, the proof chain
 * over whatever it committed must verify. Reporting success while leaving the
 * chain inconsistent would be a real defect regardless of how broken the input
 * was.
 *
 * This test is only meaningful in a sanitized build. Without ASAN an
 * out-of-bounds read inside a 64 MiB arena usually hits mapped memory and goes
 * unnoticed, so an unsanitized pass is weak evidence:
 *
 *   cmake -S . -B build-asan -G Ninja -DCMAKE_BUILD_TYPE=Debug \
 *         -DKHZ_ENABLE_ASAN=ON -DKHZ_ENABLE_UBSAN=ON -DKHZ_BUILD_SHARED=OFF
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "khz_arena.h"
#include "khz_sheet.h"
#include "khz_xlsx_reader.h"

#define KHZ_FUZZ_TRUNCATION_STEP 8
#define KHZ_FUZZ_CORRUPTIONS     512

/* Fixed seed: every run tries the same 512 offsets, so a failure can be
 * reproduced and bisected instead of appearing once and never again. */
#define KHZ_FUZZ_SEED 0x2545f4914f6cdd1dULL

static uint64_t fuzz_next(uint64_t *state)
{
    /* xorshift64*, chosen because it is four lines and needs no library. This
     * only has to spread offsets across the file. */
    uint64_t x = *state;
    x ^= x >> 12;
    x ^= x << 25;
    x ^= x >> 27;
    *state = x;
    return x * 0x2545f4914f6cdd1dULL;
}

static size_t cases_run;
static size_t cases_accepted;
static size_t cases_rejected;
static int    defects;

/* Runs one broken archive all the way through and tears the sheet down again.
 *
 * A fresh sheet per case is deliberate. Reusing one would let state from an
 * earlier case explain a later result, and the point of a fixed seed is that
 * case N means the same thing on every run. It also means each case gets a
 * clean arena, so an arena rejection is attributable to this input alone.
 */
static void run_case(const unsigned char *bytes, size_t size, const char *what,
                     size_t index)
{
    KhzSheet sheet;
    if (khz_sheet_init_default(&sheet) != KHZ_SHEET_OK) {
        printf("FAIL %s case %zu: sheet init failed\n", what, index);
        defects++;
        return;
    }

    KhzXlsxReader reader;
    if (khz_xlsx_reader_init(&reader, khz_sheet_arena(&sheet)) != KHZ_SHEET_OK) {
        printf("FAIL %s case %zu: reader init failed\n", what, index);
        defects++;
        khz_sheet_destroy(&sheet);
        return;
    }

    /* Any return value is acceptable. The contract being tested is that this
     * call returns at all, without touching memory it does not own. */
    KhzSheetStatus st = khz_xlsx_reader_read(&reader, &sheet, bytes, size);

    cases_run++;
    if (st == KHZ_SHEET_OK) {
        cases_accepted++;

        /* The one invariant that survives arbitrary input corruption: having
         * claimed success, the chain over what was committed must verify. */
        size_t failed_index = 0;
        if (khz_sheet_verify_chain(&sheet, &failed_index) != KHZ_SHEET_OK) {
            printf("FAIL %s case %zu (size %zu): read returned OK but the "
                   "proof chain does not verify at index %zu\n",
                   what, index, size, failed_index);
            defects++;
        }
    } else {
        cases_rejected++;
    }

    khz_xlsx_reader_reset(&reader);
    khz_sheet_destroy(&sheet);
}

int main(int argc, char **argv)
{
    if (argc != 2) {
        fprintf(stderr, "usage: khz_fuzz_test <fixture.xlsx>\n");
        return 2;
    }

    FILE *f = fopen(argv[1], "rb");
    if (!f) {
        fprintf(stderr, "cannot open fixture: %s\n", argv[1]);
        return 2;
    }
    if (fseek(f, 0, SEEK_END) != 0) {
        fclose(f);
        return 2;
    }
    long end = ftell(f);
    if (end <= 0 || fseek(f, 0, SEEK_SET) != 0) {
        fclose(f);
        return 2;
    }
    size_t size = (size_t)end;

    unsigned char *good = (unsigned char *)malloc(size);
    unsigned char *work = (unsigned char *)malloc(size);
    if (!good || !work || fread(good, 1, size, f) != size) {
        fprintf(stderr, "cannot read fixture\n");
        free(good);
        free(work);
        fclose(f);
        return 2;
    }
    fclose(f);

    printf("fixture     = %s (%zu bytes)\n", argv[1], size);

    /* Sanity: the unmodified fixture must still load. If this fails, the
     * results below say nothing about corruption handling - the harness itself
     * is broken or the fixture is not what it should be. */
    {
        KhzSheet sheet;
        KhzXlsxReader reader;
        KhzSheetStatus st = KHZ_SHEET_ERR_STATE;
        if (khz_sheet_init_default(&sheet) == KHZ_SHEET_OK) {
            if (khz_xlsx_reader_init(&reader, khz_sheet_arena(&sheet)) == KHZ_SHEET_OK) {
                st = khz_xlsx_reader_read(&reader, &sheet, good, size);
                khz_xlsx_reader_reset(&reader);
            }
            khz_sheet_destroy(&sheet);
        }
        printf("baseline    = %s\n", khz_sheet_status_name(st));
        if (st != KHZ_SHEET_OK) {
            printf("FAIL baseline: the intact fixture does not load; "
                   "corruption results would be meaningless\n");
            free(good);
            free(work);
            return 1;
        }
    }

    /* Truncation. Every zip structure is length-prefixed or located by an
     * offset stored elsewhere, so cutting the file short is the most direct
     * way to produce a header that promises more than the buffer holds. Size
     * zero is included: a reader that indexes before checking is caught here. */
    for (size_t len = 0; len < size; len += KHZ_FUZZ_TRUNCATION_STEP) {
        memcpy(work, good, len);
        run_case(work, len, "truncate", len);
    }
    run_case(good, size - 1, "truncate", size - 1);

    /* Single-byte corruption. Hits compressed payloads, Huffman tables, CRC
     * fields, sizes, offsets and signatures indiscriminately, which is the
     * point: a targeted set would only test the failures already imagined. */
    uint64_t state = KHZ_FUZZ_SEED;
    for (size_t i = 0; i < KHZ_FUZZ_CORRUPTIONS; i++) {
        memcpy(work, good, size);
        size_t offset = (size_t)(fuzz_next(&state) % size);
        work[offset] ^= 0xFF;
        run_case(work, size, "corrupt", offset);
    }

    free(good);
    free(work);

    /* accepted is reported, not asserted. Corrupting a byte inside a payload
     * whose CRC-32 is checked will usually be rejected, but corrupting one in
     * an unused field legitimately still loads. Both are correct behaviour;
     * only a crash or a broken chain is not. */
    printf("cases       = %zu (accepted=%zu rejected=%zu)\n",
           cases_run, cases_accepted, cases_rejected);
    printf("%s defects=%d\n", defects == 0 ? "ALL PASS" : "FAILURES", defects);

    return defects == 0 ? 0 : 1;
}
