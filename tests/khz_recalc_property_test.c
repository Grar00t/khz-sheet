#include "khz_formula.h"
#include "khz_sheet.h"
#include <stdio.h>
#define CHECK(c) do { ++checks; if (!(c)) { fprintf(stderr,"FAIL line %d\n",__LINE__); goto fail; } } while (0)
static uint32_t state=UINT32_C(0x4b485a31);
static uint32_t next(void) { state^=state<<13; state^=state>>17; state^=state<<5; return state; }
static int graph_valid(const KhzSheet *s)
{
    uint32_t degree[32]={0}; uint64_t edges=0;
    for (size_t i=0;i<s->grid.cell_count;++i) {
        const KhzDepEdge *e=s->deps.heads[i];
        while (e!=NULL) {
            uintptr_t p=(uintptr_t)e, base=(uintptr_t)s->arena.base;
            if (p<base || p-base>khz_arena_used(&s->arena)-sizeof *e) return 0;
            if (++edges>s->deps.edge_count || e->to>=s->grid.cell_count) return 0;
            ++degree[e->to]; e=e->next;
        }
    }
    if (edges!=s->deps.edge_count) return 0;
    for (size_t i=0;i<s->grid.cell_count;++i) if (degree[i]!=s->deps.indegree[i]) return 0;
    return 1;
}
int main(void)
{
    size_t checks=0; KhzSheet incremental={0}, full={0};
    for (int trial=0;trial<100;++trial) {
        unsigned left[32]={0},right[32]={0}; int64_t expected[32]={0};
        CHECK(khz_sheet_init(&incremental,(size_t)4<<20,32)==KHZ_SHEET_OK);
        CHECK(khz_sheet_init(&full,(size_t)4<<20,32)==KHZ_SHEET_OK);
        for (unsigned i=0;i<32;++i) {
            if (i<8) {
                expected[i]=(int64_t)(next()%101);
                CHECK(khz_sheet_set_i64(&incremental,0,i,expected[i])==KHZ_SHEET_OK);
                CHECK(khz_sheet_set_i64(&full,0,i,expected[i])==KHZ_SHEET_OK);
            } else {
                char source[32]; left[i]=next()%i; right[i]=next()%i;
                int length=snprintf(source,sizeof source,"A%u+A%u",left[i]+1,right[i]+1);
                CHECK(length>0 && (size_t)length<sizeof source);
                CHECK(khz_formula_set(&incremental,0,i,source,(size_t)length,NULL)==KHZ_SHEET_OK);
                CHECK(khz_formula_set(&full,0,i,source,(size_t)length,NULL)==KHZ_SHEET_OK);
            }
        }
        for (int mutation=0;mutation<20;++mutation) {
            unsigned input=next()%8; expected[input]=(int64_t)(next()%201)-100;
            CHECK(khz_sheet_set_i64(&incremental,0,input,expected[input])==KHZ_SHEET_OK);
            CHECK(khz_sheet_set_i64(&full,0,input,expected[input])==KHZ_SHEET_OK);
            for (unsigned i=8;i<32;++i) {
                expected[i]=expected[left[i]]+expected[right[i]];
                full.grid.cells[i].flags|=KHZ_CELL_FLAG_DIRTY;
            }
            CHECK(khz_formula_recalc(&incremental,NULL)==KHZ_SHEET_OK);
            CHECK(khz_formula_recalc(&full,NULL)==KHZ_SHEET_OK);
            CHECK(graph_valid(&incremental) && graph_valid(&full));
            for (unsigned i=0;i<32;++i) {
                const KhzCell *a=NULL,*b=NULL;
                CHECK(khz_sheet_get(&incremental,0,i,&a)==KHZ_SHEET_OK);
                CHECK(khz_sheet_get(&full,0,i,&b)==KHZ_SHEET_OK);
                CHECK(a->value.num==expected[i] && a->value.den==1 && a->error==0);
                CHECK(a->kind==b->kind && a->value.num==b->value.num && a->value.den==b->value.den
                    && a->error==b->error && a->flags==b->flags);
            }
            CHECK(khz_sheet_verify_chain(&incremental,NULL)==KHZ_SHEET_OK);
            CHECK(khz_sheet_verify_chain(&full,NULL)==KHZ_SHEET_OK);
        }
        khz_sheet_destroy(&incremental); khz_sheet_destroy(&full);
    }
    printf("workbooks=100 mutations=2000 checks=%zu failures=0\n",checks); return 0;
fail:
    khz_sheet_destroy(&incremental); khz_sheet_destroy(&full); return 1;
}
