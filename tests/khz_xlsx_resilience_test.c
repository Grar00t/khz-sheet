#include "khz_formula.h"
#include "khz_xlsx_reader.h"

#include <stdio.h>
#include <string.h>

#define CHECK(c) do { \
    ++checks; \
    if (!(c)) { \
        fprintf(stderr, "FAIL line %d: %s\n", __LINE__, #c); \
        ++failures; \
    } \
} while (0)

static const char shared_xml[] =
    "<x:sst xmlns:x=\"urn:test\">"
    "<x:si><x:t>A&B&#65;&#xD;_x000D_<![CDATA[C<d]]>"
    "_x005F_x0041__xD83D__xDE00_</x:t></x:si>"
    "<x:si><x:t>bad&#x110000;</x:t></x:si>"
    "</x:sst>";

static const char sheet_xml[] =
    "<x:worksheet xmlns:x=\"urn:test\"><x:sheetData>"
    "<x:row r=\"2\">"
    "<x:c t=\"s\"><x:v>0</x:v></x:c>"
    "<x:c><x:v>not-a-number</x:v></x:c>"
    "<x:c><x:v>99999999999999999999</x:v></x:c>"
    "<x:c t=\"e\"><x:v>#N/A</x:v></x:c>"
    "<x:c><x:f>IF(A1,1,0)</x:f></x:c>"
    "<x:c><x:v>0.33333333333333331</x:v></x:c>"
    "</x:row>"
    "<x:row>"
    "<x:c t=\"inlineStr\"><x:is><x:t><![CDATA[ok<&]]></x:t></x:is></x:c>"
    "<x:c t=\"b\"><x:v>2</x:v></x:c>"
    "<x:c t=\"s\"><x:v>1</x:v></x:c>"
    "</x:row>"
    "<x:row r=\"4\">"
    "<x:c><x:v>2</x:v></x:c>"
    "<x:c><x:f>A4+1</x:f></x:c>"
    "</x:row>"
    "</x:sheetData></x:worksheet>";

int main(void)
{
    KhzArena reader_arena;
    KhzSheet sheet;
    KhzXlsxReader reader;
    KhzXlsxEntry entries[2];
    const KhzCell *cell = NULL;
    KhzXlsxReadReport report;
    uint64_t evaluated = 0u;
    int checks = 0;
    int failures = 0;
    static const char expected_shared[] =
        "A&BA\r\rC<d_x0041_"
        "\xF0\x9F\x98\x80";

    if (khz_arena_init(&reader_arena, (size_t)1 << 20) != KHZ_ARENA_OK) return 2;
    if (khz_sheet_init(&sheet, (size_t)2 << 20, 64u) != KHZ_SHEET_OK) {
        khz_arena_destroy(&reader_arena);
        return 2;
    }
    CHECK(khz_xlsx_reader_init(&reader, &reader_arena) == KHZ_SHEET_OK);

    memset(entries, 0, sizeof entries);
    entries[0].name = "xl/sharedStrings.xml";
    entries[0].name_len = strlen(entries[0].name);
    entries[0].data = (const unsigned char *)shared_xml;
    entries[0].size = sizeof shared_xml - 1u;
    entries[1].name = "xl/worksheets/sheet1.xml";
    entries[1].name_len = strlen(entries[1].name);
    entries[1].data = (const unsigned char *)sheet_xml;
    entries[1].size = sizeof sheet_xml - 1u;
    reader.entries = entries;
    reader.entry_count = 2u;
    reader.loaded = 1;

    CHECK(khz_xlsx_reader_shared_strings(&reader) == KHZ_SHEET_OK);
    CHECK(reader.string_count == 2u);
    CHECK(reader.strings != NULL && reader.strings[0] != NULL);
    CHECK(strcmp(reader.strings[0], expected_shared) == 0);
    CHECK(reader.strings[1] == NULL);
    CHECK(khz_xlsx_reader_parse_sheet(&reader, &sheet,
                                      "xl/worksheets/sheet1.xml") == KHZ_SHEET_OK);
    CHECK(khz_xlsx_reader_report(&reader, &report) == KHZ_SHEET_OK);
    CHECK(report.shared_strings == 2u);
    CHECK(report.cells_seen == 11u);
    CHECK(report.cells_loaded == 11u);
    CHECK(report.cells_unsupported == 4u);
    CHECK(report.formulas_seen == 2u);

    CHECK(khz_sheet_get(&sheet, 0u, 1u, &cell) == KHZ_SHEET_OK);
    CHECK(cell != NULL && cell->kind == (uint32_t)KHZ_CELL_TEXT);
    CHECK(cell != NULL && cell->text_len == strlen(expected_shared));
    CHECK(cell != NULL && memcmp(cell->text, expected_shared, cell->text_len) == 0);

    CHECK(khz_sheet_get(&sheet, 1u, 1u, &cell) == KHZ_SHEET_OK);
    CHECK(cell != NULL && cell->kind == (uint32_t)KHZ_CELL_ERROR
          && cell->error == (uint32_t)KHZ_CELL_ERROR_VALUE);
    CHECK(khz_sheet_get(&sheet, 2u, 1u, &cell) == KHZ_SHEET_OK);
    CHECK(cell != NULL && cell->kind == (uint32_t)KHZ_CELL_ERROR
          && cell->error == (uint32_t)KHZ_CELL_ERROR_NUM);
    CHECK(khz_sheet_get(&sheet, 3u, 1u, &cell) == KHZ_SHEET_OK);
    CHECK(cell != NULL && cell->kind == (uint32_t)KHZ_CELL_ERROR
          && cell->error == (uint32_t)KHZ_CELL_ERROR_NA);

    CHECK(khz_sheet_get(&sheet, 4u, 1u, &cell) == KHZ_SHEET_OK);
    CHECK(cell != NULL && cell->kind == (uint32_t)KHZ_CELL_FORMULA);
    CHECK(cell != NULL && cell->formula_len == 10u
          && memcmp(cell->formula, "IF(A1,1,0)", 10u) == 0);

    CHECK(khz_sheet_get(&sheet, 5u, 1u, &cell) == KHZ_SHEET_OK);
    CHECK(cell != NULL && cell->kind == (uint32_t)KHZ_CELL_RATIONAL);
    CHECK(cell != NULL && cell->value.num == INT64_C(33333333333333331));
    CHECK(cell != NULL && cell->value.den == INT64_C(100000000000000000));

    CHECK(khz_sheet_get(&sheet, 0u, 2u, &cell) == KHZ_SHEET_OK);
    CHECK(cell != NULL && cell->kind == (uint32_t)KHZ_CELL_TEXT
          && cell->text_len == 4u && memcmp(cell->text, "ok<&", 4u) == 0);
    CHECK(khz_sheet_get(&sheet, 1u, 2u, &cell) == KHZ_SHEET_OK);
    CHECK(cell != NULL && cell->kind == (uint32_t)KHZ_CELL_ERROR
          && cell->error == (uint32_t)KHZ_CELL_ERROR_VALUE);
    CHECK(khz_sheet_get(&sheet, 2u, 2u, &cell) == KHZ_SHEET_OK);
    CHECK(cell != NULL && cell->kind == (uint32_t)KHZ_CELL_ERROR
          && cell->error == (uint32_t)KHZ_CELL_ERROR_VALUE);

    CHECK(khz_sheet_get(&sheet, 0u, 3u, &cell) == KHZ_SHEET_OK);
    CHECK(cell != NULL && cell->kind == (uint32_t)KHZ_CELL_RATIONAL
          && cell->value.num == 2 && cell->value.den == 1);
    CHECK(khz_sheet_get(&sheet, 1u, 3u, &cell) == KHZ_SHEET_OK);
    CHECK(cell != NULL && cell->kind == (uint32_t)KHZ_CELL_FORMULA);
    CHECK(khz_dep_edge_count(&sheet.deps) >= 1u);
    CHECK(khz_formula_recalc(&sheet, &evaluated) == KHZ_SHEET_OK);
    CHECK(evaluated == 2u);

    CHECK(khz_sheet_get(&sheet, 4u, 1u, &cell) == KHZ_SHEET_OK);
    CHECK(cell != NULL && cell->kind == (uint32_t)KHZ_CELL_FORMULA
          && cell->error == (uint32_t)KHZ_CELL_ERROR_NONE);
    CHECK(cell != NULL && cell->formula_len == 10u
          && memcmp(cell->formula, "IF(A1,1,0)", 10u) == 0);
    CHECK(cell != NULL && cell->value.num == 0 && cell->value.den == 1);

    CHECK(khz_sheet_get(&sheet, 1u, 3u, &cell) == KHZ_SHEET_OK);
    CHECK(cell != NULL && cell->kind == (uint32_t)KHZ_CELL_FORMULA
          && cell->error == (uint32_t)KHZ_CELL_ERROR_NONE);
    CHECK(cell != NULL && cell->value.num == 3 && cell->value.den == 1);
    CHECK(khz_sheet_verify_chain(&sheet, NULL) == KHZ_SHEET_OK);

    khz_sheet_destroy(&sheet);
    khz_arena_destroy(&reader_arena);
    printf("checks=%d failures=%d\n", checks, failures);
    return failures != 0 ? 1 : 0;
}
