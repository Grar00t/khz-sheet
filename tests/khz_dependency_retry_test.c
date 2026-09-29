#include "khz_sheet.h"

#include <stdint.h>
#include <stdio.h>

#define CHECK(expr) do { \
    ++checks; \
    if (!(expr)) { \
        fprintf(stderr, "FAIL line %d: %s\n", __LINE__, #expr); \
        ++failures; \
    } \
} while (0)

static int edge_count_to(const KhzDepGraph *graph, size_t source, size_t target)
{
    int count = 0;
    const KhzDepEdge *edge = graph->heads[source];
    while (edge != NULL) {
        if (edge->to == target) ++count;
        edge = edge->next;
    }
    return count;
}

int main(void)
{
    KhzSheet sheet;
    size_t a1 = 0, a2 = 0, b1 = 0;
    int checks = 0, failures = 0;
    CHECK(khz_sheet_init(&sheet, (size_t)1 << 20, 8) == KHZ_SHEET_OK);
    CHECK(khz_sheet_set_i64(&sheet, 0, 0, 1) == KHZ_SHEET_OK);
    CHECK(khz_sheet_set_i64(&sheet, 0, 1, 2) == KHZ_SHEET_OK);
    CHECK(khz_sheet_set_i64(&sheet, 1, 0, 0) == KHZ_SHEET_OK);

    CHECK(khz_grid_find(&sheet.grid, 0, 0, &a1, NULL) == KHZ_SHEET_OK);
    CHECK(khz_grid_find(&sheet.grid, 0, 1, &a2, NULL) == KHZ_SHEET_OK);
    CHECK(khz_grid_find(&sheet.grid, 1, 0, &b1, NULL) == KHZ_SHEET_OK);

    /* Force a deterministic mid-scan failure without corrupting arena
       ownership: the first link raises indegree to UINT32_MAX, so the second
       link fails before allocation. */
    sheet.deps.indegree[b1] = UINT32_MAX - 1u;
    CHECK(khz_dep_add_range_edge(&sheet.deps, 0, 0, 0, 1, 1, 0)
          == KHZ_SHEET_ERR_OVERFLOW);
    CHECK(sheet.deps.ranges != NULL);
    CHECK(sheet.deps.ranges->scanned == a2);
    CHECK(sheet.deps.edge_count == 1u);
    CHECK(edge_count_to(&sheet.deps, a1, b1) == 1);
    CHECK(edge_count_to(&sheet.deps, a2, b1) == 0);
    /* Restore the counter to the state represented by the one real edge and
       retry. The failed cell is revisited; cells before it are not linked a
       second time. */
    sheet.deps.indegree[b1] = 1u;
    CHECK(khz_dep_range_sync(&sheet.deps) == KHZ_SHEET_OK);
    CHECK(sheet.deps.edge_count == 2u);
    CHECK(edge_count_to(&sheet.deps, a1, b1) == 1);
    CHECK(edge_count_to(&sheet.deps, a2, b1) == 1);

    CHECK(khz_dep_range_sync(&sheet.deps) == KHZ_SHEET_OK);
    CHECK(sheet.deps.edge_count == 2u);
    CHECK(edge_count_to(&sheet.deps, a1, b1) == 1);
    CHECK(edge_count_to(&sheet.deps, a2, b1) == 1);

    khz_sheet_destroy(&sheet);
    printf("checks=%d failures=%d\n", checks, failures);
    return failures ? 1 : 0;
}
