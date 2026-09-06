#ifndef KHZ_GRID_H
#define KHZ_GRID_H

#include <stddef.h>
#include <stdint.h>

#include "khz_arena.h"
#include "khz_cell.h"
#include "khz_status.h"

#ifdef __cplusplus
extern "C" {
#endif

/* The real grid ceilings, matching CellAddress.cs. Last column is XFD. */
#define KHZ_GRID_MAX_COLUMNS ((uint32_t)16384)
#define KHZ_GRID_MAX_ROWS    ((uint32_t)1048576)

/* Above this the slot table cannot be sized without overflowing size_t on a
   32-bit target, so it is refused rather than truncated. */
#define KHZ_GRID_MAX_CELLS   ((size_t)1 << 26)

typedef struct KhzGridSlot {
    uint64_t key;
    size_t   index;
    uint32_t used;
    uint32_t pad;
} KhzGridSlot;

/* Virtual grid: dense storage for the cells that exist, an open-addressed
   index for the coordinates that name them. A blank cell costs nothing, so
   the addressable space is the full 16384 x 1048576 without reserving it.

   Both arrays come from one arena in one shot at init. The table never grows:
   growing would mean a second allocation and a rehash, and the point of a
   single pool is that the high-water mark is decided before the first cell,
   not discovered under load. Exceeding cell_capacity is a counted rejection
   and KHZ_SHEET_ERR_LIMIT.

   Cell storage is append-only. An index, once handed out, refers to the same
   cell for the lifetime of the grid, and cell_count only ever increases. The
   range-edge machinery below depends on both of those properties. */
typedef struct KhzGrid {
    KhzArena    *arena;
    KhzGridSlot *slots;
    size_t       slot_count;   /* power of two */
    size_t       slot_mask;
    KhzCell     *cells;
    size_t       cell_capacity;
    size_t       cell_count;
    uint64_t     probes;
    uint64_t     rejections;
} KhzGrid;

/* cell_capacity must be in 1..KHZ_GRID_MAX_CELLS. The slot table is sized to
   the next power of two at or above 2 * cell_capacity, so the load factor
   never exceeds 0.5 and linear probing stays short. */
KhzSheetStatus khz_grid_init(KhzGrid *grid, KhzArena *arena, size_t cell_capacity);

/* Finds the cell, or creates an EMPTY one at that coordinate. index and cell
   are both optional. */
KhzSheetStatus khz_grid_upsert(KhzGrid *grid, uint32_t col, uint32_t row,
                               size_t *index, KhzCell **cell);

/* KHZ_SHEET_ERR_MISSING when the coordinate has no cell. */
KhzSheetStatus khz_grid_find(const KhzGrid *grid, uint32_t col, uint32_t row,
                             size_t *index, KhzCell **cell);

size_t   khz_grid_count(const KhzGrid *grid);
size_t   khz_grid_capacity(const KhzGrid *grid);
uint64_t khz_grid_probes(const KhzGrid *grid);
uint64_t khz_grid_rejections(const KhzGrid *grid);

/* Dense iteration in insertion order. index must be below khz_grid_count. */
KhzSheetStatus khz_grid_cell_at(const KhzGrid *grid, size_t index, KhzCell **cell);

/* ---------------------------------------------------------------------- */

typedef struct KhzDepEdge {
    size_t             to;
    struct KhzDepEdge *next;
} KhzDepEdge;

/* An inclusive rectangle in cell coordinates, held normalised so that
   col1 <= col2 and row1 <= row2. khz_dep_add_range_edge normalises whatever
   corner order it is handed, so A3:C1 and C1:A3 describe the same region. */
typedef struct KhzDepRect {
    uint32_t col1;
    uint32_t row1;
    uint32_t col2;
    uint32_t row2;
} KhzDepRect;

/* A dependency on a region rather than on a cell.

   Before Phase 96 a range reference such as SUM(A1:A100) was lowered into one
   concrete edge per cell that happened to exist at the moment the formula was
   declared. That froze the range: a cell written into A50 afterwards was
   inside the region the formula reads, but no edge pointed at the formula, so
   recalculation never reached it and the sheet reported a stale sum. The bug
   was silent, which is the worst property a dependency bug can have.

   A range edge instead stores the region itself. `scanned` records how many
   grid cells have already been examined for this region. Because cell storage
   is append-only and indices are stable, every cell created after the last
   sync occupies an index at or above `scanned`, so khz_dep_range_sync only
   has to walk the tail, and no (region, cell) pair is ever linked twice.

   A cell inside a region that feeds a formula in that same region is a
   genuine circular reference. The self-edge is recorded exactly like any
   other, so khz_dep_topo reports KHZ_SHEET_ERR_CYCLE. It is not skipped:
   skipping it would break the cycle silently and yield a number that looks
   like an answer. */
typedef struct KhzDepRangeEdge {
    KhzDepRect              rect;
    size_t                  to;      /* dependent cell index */
    size_t                  scanned; /* grid cells already examined */
    struct KhzDepRangeEdge *next;
} KhzDepRangeEdge;

/* Dependency graph over grid cell indices. An edge from A to B means B reads A,
   so A must be evaluated first. Edge nodes are arena allocated and never
   freed individually, like everything else here. */
typedef struct KhzDepGraph {
    KhzArena         *arena;
    KhzGrid          *grid;
    KhzDepEdge      **heads;      /* one list head per cell slot */
    uint32_t         *indegree;
    size_t            capacity;
    uint64_t          edge_count;
    KhzDepRangeEdge  *ranges;     /* declared regions, newest first */
    uint64_t          range_count;
    uint64_t          range_links; /* concrete edges materialised from regions */
} KhzDepGraph;

KhzSheetStatus khz_dep_init(KhzDepGraph *graph, KhzArena *arena, KhzGrid *grid);

/* Both endpoints are upserted, so an edge may be declared before either cell
   has a value. A duplicate edge is added again and counted again; the topo
   order is unaffected because indegree and edge count move together. */
KhzSheetStatus khz_dep_add_edge(KhzDepGraph *graph,
                               uint32_t from_col, uint32_t from_row,
                               uint32_t to_col, uint32_t to_row);

/* Declares that the cell at (to_col, to_row) reads every cell in the given
   rectangle, including cells that do not exist yet.

   The corners may be given in any order. The region is range-checked against
   the grid ceilings and refused with KHZ_SHEET_ERR_LIMIT if it falls outside
   them. Cells already present inside the region are linked before the call
   returns; later ones are linked by khz_dep_range_sync.

   Note that this deliberately does not replace khz_dep_add_edge. A single
   cell reference is still a single edge, with no region to re-scan. */
KhzSheetStatus khz_dep_add_range_edge(KhzDepGraph *graph,
                                     uint32_t col1, uint32_t row1,
                                     uint32_t col2, uint32_t row2,
                                     uint32_t to_col, uint32_t to_row);

/* Materialises concrete edges for cells that appeared inside a declared
   region since the last sync. Idempotent: calling it twice with no cells
   created in between adds nothing. khz_dep_topo calls it, so ordinary
   recalculation never has to. Call it directly only when the indegree array
   is being inspected without a topological sort. */
KhzSheetStatus khz_dep_range_sync(KhzDepGraph *graph);

/* 1 when the coordinate falls inside the rectangle. */
int khz_dep_rect_contains(const KhzDepRect *rect, uint32_t col, uint32_t row);

/* Kahn's algorithm over the cells that currently exist. order receives cell
   indices in evaluation order and must hold at least khz_grid_count entries.
   A graph with a cycle returns KHZ_SHEET_ERR_CYCLE and writes nothing: a
   circular reference is reported, never broken at an arbitrary edge.

   Range edges are synced first, so a cell written into a region after the
   formula that reads it was declared is ordered correctly on this call and
   not on some later one.

   Scratch memory is taken from the arena between a mark and a release, so the
   call leaves the arena offset exactly where it found it. */
KhzSheetStatus khz_dep_topo(KhzDepGraph *graph, size_t *order, size_t capacity,
                            size_t *count);

uint64_t khz_dep_edge_count(const KhzDepGraph *graph);
uint64_t khz_dep_range_count(const KhzDepGraph *graph);
uint64_t khz_dep_range_links(const KhzDepGraph *graph);

#ifdef __cplusplus
}
#endif

#endif /* KHZ_GRID_H */
