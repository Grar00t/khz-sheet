#include "khz_formula.h"
#include "khz_xlsx_reader.h"
#include "khz_xlsx.h"
#include <stdio.h>
#include <string.h>
#define CHECK(c) do { ++checks; if (!(c)) { fprintf(stderr,"FAIL line %d: %s\n",__LINE__,#c); ++failures; } } while (0)
static int contains(const KhzXlsxEntry *entry,const char *needle)
{
    size_t n=strlen(needle);
    if (entry==NULL || entry->size<n) return 0;
    for (size_t i=0;i<=entry->size-n;++i) if (memcmp(entry->data+i,needle,n)==0) return 1;
    return 0;
}
int main(void)
{
    KhzSheet sheet; KhzArena scratch,reader_arena; int checks=0,failures=0;
    if (khz_sheet_init(&sheet,(size_t)1<<20,16)!=KHZ_SHEET_OK) return 2;
    if (khz_arena_init(&scratch,(size_t)2<<20)!=KHZ_ARENA_OK) { khz_sheet_destroy(&sheet); return 2; }
    if (khz_arena_init(&reader_arena,(size_t)1<<20)!=KHZ_ARENA_OK) { khz_arena_destroy(&scratch); khz_sheet_destroy(&sheet); return 2; }
    CHECK(khz_sheet_set_i64(&sheet,0,0,42)==KHZ_SHEET_OK);
    CHECK(khz_formula_set(&sheet,1,0,"=A1+1",5,NULL)==KHZ_SHEET_OK);
    CHECK(khz_formula_set(&sheet,2,0,"1/3",3,NULL)==KHZ_SHEET_OK);
    CHECK(khz_formula_set(&sheet,3,0,"1/0",3,NULL)==KHZ_SHEET_OK);
    CHECK(khz_formula_recalc(&sheet,NULL)==KHZ_SHEET_OK);
    for (int dirty=0;dirty<2;++dirty) {
        const unsigned char *bytes=NULL; size_t length=0;
        KhzXlsxReport report={0}; KhzXlsxReader reader; const KhzXlsxEntry *xml=NULL,*book=NULL;
        if (dirty) CHECK(khz_sheet_set_i64(&sheet,0,0,7)==KHZ_SHEET_OK);
        uint64_t commits=sheet.commits;
        CHECK(khz_xlsx_build(&sheet,&scratch,"Cache",&bytes,&length,&report)==KHZ_SHEET_OK);
        CHECK(sheet.commits==commits && khz_sheet_verify_chain(&sheet,NULL)==KHZ_SHEET_OK);
        CHECK(khz_xlsx_reader_init(&reader,&reader_arena)==KHZ_SHEET_OK);
        CHECK(khz_xlsx_reader_load(&reader,bytes,length)==KHZ_SHEET_OK);
        CHECK(khz_xlsx_reader_find(&reader,"xl/worksheets/sheet1.xml",&xml)==KHZ_SHEET_OK);
        CHECK(khz_xlsx_reader_find(&reader,"xl/workbook.xml",&book)==KHZ_SHEET_OK);
        CHECK(contains(xml,"<f>A1+1</f>"));
        CHECK(!contains(xml,"<f>=A1+1</f>"));
        CHECK(contains(book,"fullCalcOnLoad=\"1\""));
        if (dirty) {
            CHECK(contains(xml,"<c r=\"B1\"><f>A1+1</f></c>"));
            CHECK(contains(xml,"<c r=\"C1\"><f>1/3</f></c>"));
            CHECK(contains(xml,"<c r=\"D1\"><f>1/0</f></c>"));
            CHECK(report.lossy_cells==0);
        } else {
            CHECK(contains(xml,"<c r=\"B1\"><f>A1+1</f><v>43</v></c>"));
            CHECK(contains(xml,"<c r=\"C1\"><f>1/3</f><v>"));
            CHECK(contains(xml,"<c r=\"D1\" t=\"e\"><f>1/0</f><v>#DIV/0!</v></c>"));
            CHECK(report.lossy_cells==1);
        }
        CHECK(khz_xlsx_reader_reset(&reader)==KHZ_SHEET_OK);
        CHECK(khz_arena_release(&scratch,0)==KHZ_ARENA_OK);
    }
    khz_arena_destroy(&reader_arena); khz_arena_destroy(&scratch); khz_sheet_destroy(&sheet);
    printf("checks=%d failures=%d\n",checks,failures); return failures?1:0;
}
