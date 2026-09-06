#include "khz_grid.h"

#include <string.h>

#include "khz_hash.h"

static void khz_store_le64(unsigned char *p, uint64_t value)
{
    unsigned int i;
    for (i = 0u; i < 8u; ++i) {
        p[i] = (unsigned char)((value >> (8u * i)) & 0xffu);
    }
}

/* Hashing the key through a fixed little-endian encoding rather than hashing
   the raw bytes of a uint64_t keeps bucket assignment identical on a
   big-endian machine. A grid that placed cells differently per host would
   produce a different probe count for the same workload, which would make any
   measurement of it unreproducible. */
static uint64_t khz_grid_hash_key(uint64_t key)
{
    unsigned char encoded[8];

    khz_store_le64(encoded, key);
    return khz_fnv1a64(encoded, sizeof encoded);
}

static size_t khz_next_pow2(size_t value)
{
    size_t result = (size_t)1;

    while (result < value) {
        size_t doubled = result << 1;

        if (doubled <= result) {
            return (size_t)0; /* would overflow */
        }

        result = doubled;
    }

    return result;
}

KhzSheetStatus khz_grid_init(KhzGrid *grid, KhzArena *arena, size_t cell_capacity)
{
    size_t slot_count;
    KhzGridSlot *slots;
    KhzCell *cells;

    if (grid == NULL || arena == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    if (cell_capacity == (size_t)0 || cell_capacity > KHZ_GRID_MAX_CELLS) {
        return KHZ_SHEET_ERR_RANGE;
    }

    /* Load factor is capped at 0.5 by construction, so linear probing stays
       short and the table never needs to grow. */
    slot_count = khz_next_pow2(cell_capacity * (size_t)2);
    if (slot_count == (size_t)0) {
        return KHZ_SHEET_ERR_RANGE;
    }
    if (slot_count < (size_t)64) {
        slot_count = (size_t)64;
    }

    memset(grid, 0, sizeof *grid);

    /* Two allocations from the pool, both at init, both permanent. After this
       point the grid never allocates again: an insert either fits in the space
       already reserved or is rejected. */
    slots = (KhzGridSlot *)khz_arena_alloc_zeroed(arena, slot_count * sizeof *slots);
    if (slots == NULL) {
        return KHZ_SHEET_ERR_MEMORY;
    }

    cells = (KhzCell *)khz_arena_alloc_zeroed(arena, cell_capacity * sizeof *cells);
    if (cells == NULL) {
        return KHZ_SHEET_ERR_MEMORY;
    }

    grid->arena = arena;
    grid->slots = slots;
    grid->slot_count = slot_count;
    grid->slot_mask = slot_count - (size_t)1;
    grid->cells = cells;
    grid->cell_capacity = cell_capacity;
    grid->cell_count = (size_t)0;

    return KHZ_SHEET_OK;
}

/* Locates the slot holding key, or the first free slot on its probe chain.
   Returns 1 when the key was found, 0 when *slot_out is where it would go.
   The table is never full, because cell_count is capped at half slot_count,
   so the loop always terminates on a free slot. */
static int khz_grid_probe(const KhzGrid *grid, uint64_t key, size_t *slot_out,
                          uint64_t *probes)
{
    size_t index = (size_t)(khz_grid_hash_key(key) & (uint64_t)grid->slot_mask);
    size_t steps = (size_t)0;

    for (;;) {
        const KhzGridSlot *slot = &grid->slots[index];

        ++steps;

        if (slot->used == 0u) {
            *slot_out = index;
            if (probes != NULL) {
                *probes += (uint64_t)steps;
            }
            return 0;
        }

        if (slot->key == key) {
            *slot_out = index;
            if (probes != NULL) {
                *probes += (uint64_t)steps;
            }
            return 1;
        }

        index = (index + (size_t)1) & grid->slot_mask;
    }
}

KhzSheetStatus khz_grid_upsert(KhzGrid *grid, uint32_t col, uint32_t row,
                               size_t *index, KhzCell **cell)
{
    uint64_t key;
    size_t slot_index;
    KhzSheetStatus status;

    if (grid == NULL || grid->slots == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    if (col >= KHZ_GRID_MAX_COLUMNS || row >= KHZ_GRID_MAX_ROWS) {
        return KHZ_SHEET_ERR_LIMIT;
    }

    key = khz_cell_key(col, row);

    if (khz_grid_probe(grid, key, &slot_index, &grid->probes) != 0) {
        KhzGridSlot *slot = &grid->slots[slot_index];

        if (index != NULL) {
            *index = slot->index;
        }
        if (cell != NULL) {
            *cell = &grid->cells[slot->index];
        }
        return KHZ_SHEET_OK;
    }

    if (grid->cell_count >= grid->cell_capacity) {
        /* Counted, not absorbed. The caller sized the pool; exceeding it is
           information, and hiding it behind a heap allocation would destroy
           the only guarantee the single-pool design offers. */
        ++grid->rejections;
        return KHZ_SHEET_ERR_LIMIT;
    }

    status = khz_cell_init_empty(&grid->cells[grid->cell_count], col, row);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    grid->slots[slot_index].key = key;
    grid->slots[slot_index].index = grid->cell_count;
    grid->slots[slot_index].used = 1u;

    if (index != NULL) {
        *index = grid->cell_count;
    }
    if (cell != NULL) {
        *cell = &grid->cells[grid->cell_count];
    }

    ++grid->cell_count;
    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_grid_find(const KhzGrid *grid, uint32_t col, uint32_t row,
                             size_t *index, KhzCell **cell)
{
    uint64_t key;
    size_t slot_index;

    if (grid == NULL || grid->slots == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    if (col >= KHZ_GRID_MAX_COLUMNS || row >= KHZ_GRID_MAX_ROWS) {
        return KHZ_SHEET_ERR_LIMIT;
    }

    key = khz_cell_key(col, row);

    if (khz_grid_probe(grid, key, &slot_index, NULL) == 0) {
        return KHZ_SHEET_ERR_MISSING;
    }

    if (index != NULL) {
        *index = grid->slots[slot_index].index;
    }
    if (cell != NULL) {
        *cell = &grid->cells[grid->slots[slot_index].index];
    }

    return KHZ_SHEET_OK;
}

size_t khz_grid_count(const KhzGrid *grid)
{
    return grid == NULL ? (size_t)0 : grid->cell_count;
}

size_t khz_grid_capacity(const KhzGrid *grid)
{
    return grid == NULL ? (size_t)0 : grid->cell_capacity;
}

uint64_t khz_grid_probes(const KhzGrid *grid)
{
    return grid == NULL ? (uint64_t)0 : grid->probes;
}

uint64_t khz_grid_rejections(const KhzGrid *grid)
{
    return grid == NULL ? (uint64_t)0 : grid->rejections;
}

KhzSheetStatus khz_grid_cell_at(const KhzGrid *grid, size_t index, KhzCell **cell)
{
    if (grid == NULL || grid->cells == NULL || cell == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    if (index >= grid->cell_count) {
        return KHZ_SHEET_ERR_RANGE;
    }

    *cell = &grid->cells[index];
    return KHZ_SHEET_OK;
}

/* ---------------------------------------------------------------------- */

KhzSheetStatus khz_dep_init(KhzDepGraph *graph, KhzArena *arena, KhzGrid *grid)
{
    KhzDepEdge **heads;
    uint32_t *indegree;
    size_t capacity;

    if (graph == NULL || arena == NULL || grid == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    if (grid->cells == NULL) {
        return KHZ_SHEET_ERR_STATE;
    }

    capacity = grid->cell_capacity;

    memset(graph, 0, sizeof *graph);

    heads = (KhzDepEdge **)khz_arena_alloc_zeroed(arena, capacity * sizeof *heads);
    if (heads == NULL) {
        return KHZ_SHEET_ERR_MEMORY;
    }

    indegree = (uint32_t *)khz_arena_alloc_zeroed(arena, capacity * sizeof *indegree);
    if (indegree == NULL) {
        return KHZ_SHEET_ERR_MEMORY;
    }

    graph->arena = arena;
    graph->grid = grid;
    graph->heads = heads;
    graph->indegree = indegree;
    graph->capacity = capacity;
    graph->ranges = NULL;

    return KHZ_SHEET_OK;
}

/* The single place an edge is recorded. Both the cell-to-cell path and the
   range materialiser go through here, so indegree and edge_count cannot drift
   apart depending on which entry point was used. */
static KhzSheetStatus khz_dep_link(KhzDepGraph *graph, size_t from_index,
                                   size_t to_index)
{
    KhzDepEdge *edge;

    if (from_index >= graph->capacity || to_index >= graph->capacity) {
        return KHZ_SHEET_ERR_LIMIT;
    }

    if (graph->indegree[to_index] == UINT32_MAX) {
        return KHZ_SHEET_ERR_OVERFLOW;
    }

    edge = (KhzDepEdge *)khz_arena_alloc(graph->arena, sizeof *edge);
    if (edge == NULL) {
        return KHZ_SHEET_ERR_MEMORY;
    }

    edge->to = to_index;
    edge->next = graph->heads[from_index];
    graph->heads[from_index] = edge;

    graph->indegree[to_index] += 1u;
    graph->edge_count += (uint64_t)1;

    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_dep_add_edge(KhzDepGraph *graph,
                               uint32_t from_col, uint32_t from_row,
                               uint32_t to_col, uint32_t to_row)
{
    size_t from_index;
    size_t to_index;
    KhzSheetStatus status;

    if (graph == NULL || graph->grid == NULL || graph->heads == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    status = khz_grid_upsert(graph->grid, from_col, from_row, &from_index, NULL);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    status = khz_grid_upsert(graph->grid, to_col, to_row, &to_index, NULL);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    return khz_dep_link(graph, from_index, to_index);
}

int khz_dep_rect_contains(const KhzDepRect *rect, uint32_t col, uint32_t row)
{
    if (rect == NULL) {
        return 0;
    }

    return (col >= rect->col1 && col <= rect->col2 &&
            row >= rect->row1 && row <= rect->row2) ? 1 : 0;
}

/* Links every cell at or above `scanned` that falls inside one region, then
   advances `scanned`. Split out so that a freshly declared region and a
   later sync run identical code. */
static KhzSheetStatus khz_dep_range_scan(KhzDepGraph *graph,
                                         KhzDepRangeEdge *range)
{
    const KhzGrid *grid = graph->grid;
    size_t i;

    for (i = range->scanned; i < grid->cell_count; ++i) {
        const KhzCell *cell = &grid->cells[i];

        if (khz_dep_rect_contains(&range->rect, cell->col, cell->row) != 0) {
            /* i == range->to is not filtered out. A formula inside the region
               it reads is a circular reference, and khz_dep_topo must be the
               one to say so. */
            KhzSheetStatus status = khz_dep_link(graph, i, range->to);

            if (status != KHZ_SHEET_OK) {
                /* Stop at the failure rather than advancing scanned, so the
                   unlinked tail is retried on the next sync instead of being
                   skipped forever. */
                range->scanned = i;
                return status;
            }

            graph->range_links += (uint64_t)1;
        }
    }

    range->scanned = grid->cell_count;
    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_dep_add_range_edge(KhzDepGraph *graph,
                                     uint32_t col1, uint32_t row1,
                                     uint32_t col2, uint32_t row2,
                                     uint32_t to_col, uint32_t to_row)
{
    KhzDepRangeEdge *range;
    size_t to_index;
    KhzSheetStatus status;

    if (graph == NULL || graph->grid == NULL || graph->heads == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    if (col1 >= KHZ_GRID_MAX_COLUMNS || col2 >= KHZ_GRID_MAX_COLUMNS ||
        row1 >= KHZ_GRID_MAX_ROWS || row2 >= KHZ_GRID_MAX_ROWS) {
        return KHZ_SHEET_ERR_LIMIT;
    }

    status = khz_grid_upsert(graph->grid, to_col, to_row, &to_index, NULL);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    range = (KhzDepRangeEdge *)khz_arena_alloc(graph->arena, sizeof *range);
    if (range == NULL) {
        return KHZ_SHEET_ERR_MEMORY;
    }

    /* Normalise the corners so containment is a plain pair of comparisons and
       A3:C1 cannot describe an empty region by accident. */
    range->rect.col1 = col1 < col2 ? col1 : col2;
    range->rect.col2 = col1 < col2 ? col2 : col1;
    range->rect.row1 = row1 < row2 ? row1 : row2;
    range->rect.row2 = row1 < row2 ? row2 : row1;

    range->to = to_index;
    range->scanned = (size_t)0;
    range->next = graph->ranges;
    graph->ranges = range;
    graph->range_count += (uint64_t)1;

    /* Link what already exists. The region stays on the list either way, so a
       failure here costs nothing permanently: the tail is retried next sync. */
    return khz_dep_range_scan(graph, range);
}

KhzSheetStatus khz_dep_range_sync(KhzDepGraph *graph)
{
    KhzDepRangeEdge *range;

    if (graph == NULL || graph->grid == NULL || graph->heads == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    for (range = graph->ranges; range != NULL; range = range->next) {
        if (range->scanned < graph->grid->cell_count) {
            KhzSheetStatus status = khz_dep_range_scan(graph, range);

            if (status != KHZ_SHEET_OK) {
                return status;
            }
        }
    }

    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_dep_topo(KhzDepGraph *graph, size_t *order, size_t capacity,
                            size_t *count)
{
    size_t used;
    size_t mark;
    uint32_t *pending;
    size_t *queue;
    size_t head = (size_t)0;
    size_t tail = (size_t)0;
    size_t emitted = (size_t)0;
    size_t i;
    KhzSheetStatus status = KHZ_SHEET_OK;

    if (graph == NULL || graph->grid == NULL || order == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    /* Regions are resolved before indegree is read, not after. pending[] below
       is a snapshot, so a link materialised later in this call would not be
       accounted for and the dependent cell would be emitted too early. */
    status = khz_dep_range_sync(graph);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    used = graph->grid->cell_count;

    if (capacity < used) {
        return KHZ_SHEET_ERR_RANGE;
    }

    if (used == (size_t)0) {
        if (count != NULL) {
            *count = (size_t)0;
        }
        return KHZ_SHEET_OK;
    }

    /* Scratch comes from the arena between a mark and a release, so a sort of
       any size costs no permanent bytes and no heap traffic. The release at
       the end puts the offset back exactly where it was. */
    mark = khz_arena_mark(graph->arena);

    pending = (uint32_t *)khz_arena_alloc(graph->arena, used * sizeof *pending);
    queue = (size_t *)khz_arena_alloc(graph->arena, used * sizeof *queue);

    if (pending == NULL || queue == NULL) {
        (void)khz_arena_release(graph->arena, mark);
        return KHZ_SHEET_ERR_MEMORY;
    }

    for (i = (size_t)0; i < used; ++i) {
        pending[i] = graph->indegree[i];
        if (pending[i] == 0u) {
            queue[tail++] = i;
        }
    }

    while (head < tail) {
        size_t node = queue[head++];
        const KhzDepEdge *edge = graph->heads[node];

        order[emitted++] = node;

        while (edge != NULL) {
            if (edge->to < used && pending[edge->to] != 0u) {
                pending[edge->to] -= 1u;
                if (pending[edge->to] == 0u) {
                    queue[tail++] = edge->to;
                }
            }
            edge = edge->next;
        }
    }

    if (emitted != used) {
        /* A circular reference. It is reported, not resolved: breaking an
           arbitrary edge would produce a number that looks like an answer. */
        status = KHZ_SHEET_ERR_CYCLE;
    } else if (count != NULL) {
        *count = emitted;
    }

    (void)khz_arena_release(graph->arena, mark);
    return status;
}

uint64_t khz_dep_edge_count(const KhzDepGraph *graph)
{
    return graph == NULL ? (uint64_t)0 : graph->edge_count;
}

uint64_t khz_dep_range_count(const KhzDepGraph *graph)
{
    return graph == NULL ? (uint64_t)0 : graph->range_count;
}

uint64_t khz_dep_range_links(const KhzDepGraph *graph)
{
    return graph == NULL ? (uint64_t)0 : graph->range_links;
}
