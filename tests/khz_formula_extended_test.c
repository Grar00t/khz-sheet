#include "khz_formula.h"
#include "khz_sheet.h"
#include <stdio.h>
#include <string.h>
static int checks, failures;
#define CHECK(c) do { ++checks; if (!(c)) { ++failures; fprintf(stderr,"FAIL line %d: %s\n",__LINE__,#c); } } while (0)
static void value(KhzSheet *sheet, const char *source, int64_t n, int64_t d, unsigned error)
{
    KhzArena arena; KhzFormula formula; KhzFormulaResult result;
    CHECK(khz_arena_init(&arena, 1048576) == KHZ_ARENA_OK);
    KhzSheetStatus status = khz_formula_parse(&formula, &arena, 10, 10, source, strlen(source), NULL);
    CHECK(status == KHZ_SHEET_OK);
    if (status == KHZ_SHEET_OK) {
        status = khz_formula_eval(sheet, &formula, &result);
        if (status != KHZ_SHEET_OK || result.error != error ||
            (!error && (result.value.num != n || result.value.den != d))) {
            fprintf(stderr,"expression=%s status=%d value=%lld/%lld error=%u\n",source,(int)status,
                    (long long)result.value.num,(long long)result.value.den,result.error);
            ++failures;
        }
        ++checks;
    }
    khz_arena_destroy(&arena);
}
static void invalid(const char *source)
{
    KhzArena arena; KhzFormula formula;
    CHECK(khz_arena_init(&arena, 1048576) == KHZ_ARENA_OK);
    CHECK(khz_formula_parse(&formula, &arena, 10, 10, source, strlen(source), NULL)
          == KHZ_SHEET_ERR_FORMAT);
    khz_arena_destroy(&arena);
}
int main(void)
{
    KhzSheet sheet;
    CHECK(khz_sheet_init(&sheet, 8u<<20, 4096) == KHZ_SHEET_OK);
    CHECK(khz_sheet_set_i64(&sheet,0,0,2) == KHZ_SHEET_OK);
    CHECK(khz_sheet_set_i64(&sheet,0,1,3) == KHZ_SHEET_OK);
    CHECK(khz_sheet_set_text(&sheet,0,2,"text",4) == KHZ_SHEET_OK);
    CHECK(khz_sheet_set_bool(&sheet,0,3,1) == KHZ_SHEET_OK);
    CHECK(khz_sheet_set_error(&sheet,0,4,KHZ_CELL_ERROR_DIV0) == KHZ_SHEET_OK);
    value(&sheet,"COUNT(A1:A10)",2,1,0); value(&sheet,"COUNTA(A1:A10)",5,1,0);
    value(&sheet,"PRODUCT(A1:A4)",6,1,0); value(&sheet,"PRODUCT(1/3,3/7)",1,7,0);
    value(&sheet,"PRODUCT(B1:B2)",0,1,0); value(&sheet,"ABS(-3/7)",3,7,0);
    value(&sheet,"CEILING(-3/2)",-1,1,0); value(&sheet,"FLOOR(-3/2)",-2,1,0);
    value(&sheet,"ROUND(1.005,2)",101,100,0); value(&sheet,"ROUND(-1.005,2)",-101,100,0);
    value(&sheet,"ROUND(150,-2)",200,1,0); value(&sheet,"ROUND(-150,-2)",-200,1,0);
    value(&sheet,"ROUND(1/9223372036854775807,-18)",0,1,0);
    value(&sheet,"ROUND(9223372036854775807,18)",INT64_MAX,1,0);
    value(&sheet,"ROUND(9223372036854775807,-1)",0,1,KHZ_CELL_ERROR_NUM);
    value(&sheet,"ROUND(1,19)",0,1,KHZ_CELL_ERROR_NUM);
    value(&sheet,"ROUND(1,0.5)",0,1,KHZ_CELL_ERROR_NUM);
    value(&sheet,"IF(1<2,3/7,1/0)",3,7,0); value(&sheet,"IF(FALSE,1/0,2)",2,1,0);
    value(&sheet,"IF(1/0,1,2)",0,1,KHZ_CELL_ERROR_DIV0);
    value(&sheet,"AND(TRUE,1=1)",1,1,0);
    value(&sheet,"AND(TRUE,FALSE,1=1)",0,1,0);
    value(&sheet,"OR(FALSE,1=2)",0,1,0);
    value(&sheet,"OR(FALSE,1=1)",1,1,0);
    value(&sheet,"NOT(0)",1,1,0);
    value(&sheet,"NOT(3/7)",0,1,0);
    value(&sheet,"IFERROR(1/0,2/7)",2,7,0);
    value(&sheet,"IFERROR(5/7,1/0)",5,7,0);
    value(&sheet,"IFERROR(A5,2/7)",2,7,0);
    value(&sheet,"AND(A5,TRUE)",0,1,KHZ_CELL_ERROR_DIV0);
    value(&sheet,"IFERROR(IF(AND(TRUE,1=1),1/0,3),NOT(FALSE))",1,1,0);
    invalid("AND()");
    invalid("NOT(1,2)");
    invalid("IFERROR(1)");
    invalid("IFERROR(1,2,3)");
    value(&sheet,"1+2=3",1,1,0); value(&sheet,"1<>1",0,1,0);
    value(&sheet,"IF(2>=2,IF(2<=1,0,7),3)",7,1,0);
    for (int64_t n=-1000;n<=1000;++n) {
        KhzRational a, rounded, floor_value, ceiling_value;
        CHECK(khz_rational_make(n,7,&a)==KHZ_SHEET_OK);
        CHECK(khz_rational_round(a,0,&rounded)==KHZ_SHEET_OK);
        int64_t magnitude = n<0 ? -n : n;
        int64_t expected = magnitude/7 + (magnitude%7>=4);
        CHECK(rounded.num==(n<0 ? -expected : expected) && rounded.den==1);
        CHECK(khz_rational_floor(a,&floor_value)==KHZ_SHEET_OK);
        CHECK(khz_rational_ceiling(a,&ceiling_value)==KHZ_SHEET_OK);
        CHECK(floor_value.num*7<=n && ceiling_value.num*7>=n);
    }
    CHECK(khz_formula_set(&sheet,1,0,"IF(A1>2,PRODUCT(A1:A2),ABS(-1))",strlen("IF(A1>2,PRODUCT(A1:A2),ABS(-1))"),NULL)==KHZ_SHEET_OK);
    CHECK(khz_formula_recalc(&sheet,NULL)==KHZ_SHEET_OK);
    CHECK(khz_sheet_set_i64(&sheet,0,0,4)==KHZ_SHEET_OK);
    CHECK(khz_formula_recalc(&sheet,NULL)==KHZ_SHEET_OK);
    value(&sheet,"B1",12,1,0);
    CHECK(khz_sheet_verify_chain(&sheet,NULL)==KHZ_SHEET_OK);
    CHECK(khz_formula_set(&sheet,2,0,"IFERROR(1/A1,AND(FALSE,NOT(FALSE)))",
                          strlen("IFERROR(1/A1,AND(FALSE,NOT(FALSE)))"),NULL)==KHZ_SHEET_OK);
    CHECK(khz_formula_recalc(&sheet,NULL)==KHZ_SHEET_OK);
    value(&sheet,"C1",1,4,0);
    CHECK(khz_sheet_set_i64(&sheet,0,0,0)==KHZ_SHEET_OK);
    CHECK(khz_formula_recalc(&sheet,NULL)==KHZ_SHEET_OK);
    value(&sheet,"C1",0,1,0);
    CHECK(khz_sheet_verify_chain(&sheet,NULL)==KHZ_SHEET_OK);
    khz_sheet_destroy(&sheet);
    printf("checks=%d failures=%d\n",checks,failures);
    return failures?1:0;
}
