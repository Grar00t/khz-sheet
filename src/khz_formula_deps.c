#include "khz_formula.h"

#include <stdint.h>
#include <string.h>

#include "khz_sheet.h"

static int khz_deps_result_is_error(const KhzFormulaResult *r)
{
    return r->kind == (uint32_t)KHZ_CELL_ERROR;
}

/* Formula dependency replacement is transactional without changing the public
   graph layout. A formula tree is parsed first; dep_mark is then taken before
   any new edge or range is declared. Because the arena is monotonic, graph
   nodes below that mark are from older formula generations and nodes at or
   above it belong to this declaration attempt.

   On success, old incoming dependencies are unlinked. On declaration or cell
   installation failure, only the newly declared side is unlinked before the
   formula arena mark is released. Arena bytes from retired successful formula
   generations remain reserved, but no stale graph node remains reachable. */
static int khz_dep_node_is_new(const KhzDepGraph *graph, const void *node, size_t mark)
{
    uintptr_t base;
    uintptr_t address;
    uintptr_t end;

    if (graph == NULL || graph->arena == NULL || graph->arena->base == NULL || node == NULL) {
        return 0;
    }

    base = (uintptr_t)graph->arena->base;
    address = (uintptr_t)node;
    end = base + (uintptr_t)graph->arena->capacity;

    if (address < base || address >= end) {
        return 0;
    }

    return (size_t)(address - base) >= mark ? 1 : 0;
}

static uint64_t khz_dep_range_materialized(const KhzDepGraph *graph,
                                           const KhzDepRangeEdge *range)
{
    size_t limit;
    size_t i;
    uint64_t count = (uint64_t)0;

    if (graph == NULL || graph->grid == NULL || range == NULL) {
        return (uint64_t)0;
    }

    limit = range->scanned < graph->grid->cell_count
        ? range->scanned
        : graph->grid->cell_count;

    for (i = (size_t)0; i < limit; ++i) {
        const KhzCell *cell = &graph->grid->cells[i];
        if (khz_dep_rect_contains(&range->rect, cell->col, cell->row) != 0) {
            ++count;
        }
    }

    return count;
}

static void khz_dep_prune_target(KhzDepGraph *graph, size_t target,
                                 size_t mark, int remove_new)
{
    size_t source;
    KhzDepRangeEdge **range_link;

    if (graph == NULL || graph->grid == NULL || graph->heads == NULL ||
        target >= graph->capacity) {
        return;
    }

    for (source = (size_t)0; source < graph->grid->cell_count; ++source) {
        KhzDepEdge **link = &graph->heads[source];

        while (*link != NULL) {
            KhzDepEdge *edge = *link;
            int is_new = khz_dep_node_is_new(graph, edge, mark);

            if (edge->to == target && is_new == (remove_new != 0 ? 1 : 0)) {
                *link = edge->next;
                if (graph->indegree[target] > 0u) {
                    graph->indegree[target] -= 1u;
                }
                if (graph->edge_count > (uint64_t)0) {
                    graph->edge_count -= (uint64_t)1;
                }
                continue;
            }

            link = &edge->next;
        }
    }

    range_link = &graph->ranges;
    while (*range_link != NULL) {
        KhzDepRangeEdge *range = *range_link;
        int is_new = khz_dep_node_is_new(graph, range, mark);

        if (range->to == target && is_new == (remove_new != 0 ? 1 : 0)) {
            uint64_t links = khz_dep_range_materialized(graph, range);

            *range_link = range->next;
            if (graph->range_count > (uint64_t)0) {
                graph->range_count -= (uint64_t)1;
            }
            graph->range_links = links > graph->range_links
                ? (uint64_t)0
                : graph->range_links - links;
            continue;
        }

        range_link = &range->next;
    }
}

static int khz_formula_target_index(KhzSheet *sheet, uint32_t col, uint32_t row,
                                    size_t *index)
{
    return khz_grid_find(&sheet->grid, col, row, index, NULL) == KHZ_SHEET_OK ? 1 : 0;
}

static KhzSheetStatus khz_declare_walk(KhzSheet *sheet, const KhzFormulaNode *node,
                                       uint32_t col, uint32_t row,
                                       size_t depth, uint64_t *declared)
{
    KhzSheetStatus status;
    uint32_t i;

    if (node == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }
    if (depth > KHZ_FORMULA_MAX_DEPTH) {
        return KHZ_SHEET_ERR_LIMIT;
    }

    if (node->op == (uint32_t)KHZ_FORMULA_REF) {
        status = khz_sheet_declare_dependency(sheet, node->col0, node->row0, col, row);
        if (status != KHZ_SHEET_OK) {
            return status;
        }
        *declared += (uint64_t)1;
        return KHZ_SHEET_OK;
    }

    if (node->op == (uint32_t)KHZ_FORMULA_RANGE) {
        status = khz_dep_add_range_edge(&sheet->deps,
                                        node->col0, node->row0,
                                        node->col1, node->row1,
                                        col, row);
        if (status != KHZ_SHEET_OK) {
            return status;
        }
        *declared += (uint64_t)1;
        return KHZ_SHEET_OK;
    }

    for (i = 0u; i < node->child_count; ++i) {
        status = khz_declare_walk(sheet, node->children[i], col, row,
                                  depth + (size_t)1, declared);
        if (status != KHZ_SHEET_OK) {
            return status;
        }
    }

    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_formula_declare_dependencies(KhzSheet *sheet,
                                                const KhzFormula *formula,
                                                uint64_t *declared)
{
    uint64_t count = (uint64_t)0;
    KhzSheetStatus status;

    if (sheet == NULL || formula == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }
    if (formula->root == NULL) {
        return KHZ_SHEET_ERR_STATE;
    }

    status = khz_declare_walk(sheet, formula->root, formula->col, formula->row,
                              (size_t)0, &count);

    if (declared != NULL) {
        *declared = count;
    }

    return status;
}

KhzSheetStatus khz_formula_set(KhzSheet *sheet, uint32_t col, uint32_t row,
                               const char *source, size_t len,
                               KhzFormulaParseError *error)
{
    KhzArena *arena;
    KhzFormula formula;
    KhzSheetStatus status;
    size_t dep_mark;
    size_t target = (size_t)0;
    KhzCell *existing = NULL;

    if (sheet == NULL || source == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    arena = khz_sheet_arena(sheet);
    if (arena == NULL) {
        return KHZ_SHEET_ERR_STATE;
    }

    status = khz_grid_find(&sheet->grid, col, row, NULL, &existing);
    if (status != KHZ_SHEET_OK && status != KHZ_SHEET_ERR_MISSING) {
        return status;
    }
    if (status == KHZ_SHEET_OK && existing->revision == UINT64_MAX) {
        return KHZ_SHEET_ERR_OVERFLOW;
    }
    if (sheet->commits == UINT64_MAX || sheet->log.recorded == UINT64_MAX) {
        return KHZ_SHEET_ERR_OVERFLOW;
    }

    status = khz_formula_parse(&formula, arena, col, row, source, len, error);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    dep_mark = khz_arena_mark(arena);
    status = khz_formula_declare_dependencies(sheet, &formula, NULL);

    if (status != KHZ_SHEET_OK) {
        if (khz_formula_target_index(sheet, col, row, &target) != 0) {
            khz_dep_prune_target(&sheet->deps, target, dep_mark, 1);
        }
        khz_formula_abandon(&formula);
        return status;
    }

    status = khz_sheet_set_formula(sheet, col, row, source, len);

    if (khz_formula_target_index(sheet, col, row, &target) == 0) {
        khz_formula_abandon(&formula);
        return status == KHZ_SHEET_OK ? KHZ_SHEET_ERR_STATE : status;
    }

    if (status == KHZ_SHEET_OK) {
        khz_dep_prune_target(&sheet->deps, target, dep_mark, 0);
        return KHZ_SHEET_OK;
    }

    {
        KhzCell *current = NULL;
        int installed = 0;

        if (khz_grid_find(&sheet->grid, col, row, NULL, &current) == KHZ_SHEET_OK &&
            current->kind == (uint32_t)KHZ_CELL_FORMULA &&
            current->formula_len == (uint32_t)len &&
            (len == (size_t)0 || (current->formula != NULL &&
                                  memcmp(current->formula, source, len) == 0))) {
            installed = 1;
        }

        if (installed != 0) {
            khz_dep_prune_target(&sheet->deps, target, dep_mark, 0);
        } else {
            khz_dep_prune_target(&sheet->deps, target, dep_mark, 1);
            khz_formula_abandon(&formula);
        }
    }

    return status;
}

static void khz_dirty_dependents(KhzSheet *sheet, size_t index)
{
    const KhzDepEdge *edge;

    if (sheet->deps.heads == NULL || index >= sheet->deps.capacity) {
        return;
    }

    for (edge = sheet->deps.heads[index]; edge != NULL; edge = edge->next) {
        if (edge->to >= sheet->grid.cell_count) {
            continue;
        }
        if (sheet->grid.cells[edge->to].kind == (uint32_t)KHZ_CELL_FORMULA) {
            sheet->grid.cells[edge->to].flags |= (uint32_t)KHZ_CELL_FLAG_DIRTY;
        }
    }
}

static int khz_value_changed(const KhzCell *cell, const KhzFormulaResult *result)
{
    if (khz_deps_result_is_error(result)) {
        return cell->error != result->error ? 1 : 0;
    }

    if (cell->error != (uint32_t)KHZ_CELL_ERROR_NONE) {
        return 1;
    }

    return cell->value.num != result->value.num
        || cell->value.den != result->value.den ? 1 : 0;
}

/* Recalculation only needs ordering constraints whose dependent is currently a
   formula. A formula overwritten by a value leaves its historic graph nodes in
   the append-only arena, but those nodes no longer describe executable work.
   The generic dependency graph is left untouched; this filtered Kahn walk is a
   formula-engine view of it, so explicit graph users retain their full edges. */
static KhzSheetStatus khz_formula_evaluation_order(KhzSheet *sheet,
                                                   size_t *order,
                                                   size_t capacity,
                                                   size_t *count)
{
    size_t used;
    size_t *pending;
    size_t *queue;
    size_t head = (size_t)0;
    size_t tail = (size_t)0;
    size_t emitted = (size_t)0;
    size_t source;

    if (sheet == NULL || order == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    used = sheet->grid.cell_count;
    if (capacity < used) {
        return KHZ_SHEET_ERR_RANGE;
    }
    if (used == (size_t)0) {
        if (count != NULL) {
            *count = (size_t)0;
        }
        return KHZ_SHEET_OK;
    }

    pending = (size_t *)khz_arena_alloc_zeroed(&sheet->arena, used * sizeof *pending);
    queue = (size_t *)khz_arena_alloc(&sheet->arena, used * sizeof *queue);
    if (pending == NULL || queue == NULL) {
        return KHZ_SHEET_ERR_MEMORY;
    }

    for (source = (size_t)0; source < used; ++source) {
        const KhzDepEdge *edge;

        for (edge = sheet->deps.heads[source]; edge != NULL; edge = edge->next) {
            if (edge->to >= used) {
                return KHZ_SHEET_ERR_STATE;
            }
            if (sheet->grid.cells[edge->to].kind != (uint32_t)KHZ_CELL_FORMULA) {
                continue;
            }
            if (pending[edge->to] == SIZE_MAX) {
                return KHZ_SHEET_ERR_OVERFLOW;
            }
            pending[edge->to] += (size_t)1;
        }
    }

    for (source = (size_t)0; source < used; ++source) {
        if (pending[source] == (size_t)0) {
            queue[tail++] = source;
        }
    }

    while (head < tail) {
        size_t at = queue[head++];
        const KhzDepEdge *edge;

        order[emitted++] = at;

        for (edge = sheet->deps.heads[at]; edge != NULL; edge = edge->next) {
            if (edge->to >= used) {
                return KHZ_SHEET_ERR_STATE;
            }
            if (sheet->grid.cells[edge->to].kind != (uint32_t)KHZ_CELL_FORMULA) {
                continue;
            }
            if (pending[edge->to] == (size_t)0) {
                return KHZ_SHEET_ERR_STATE;
            }

            pending[edge->to] -= (size_t)1;
            if (pending[edge->to] == (size_t)0) {
                queue[tail++] = edge->to;
            }
        }
    }

    if (emitted != used) {
        return KHZ_SHEET_ERR_CYCLE;
    }

    if (count != NULL) {
        *count = emitted;
    }

    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_formula_recalc(KhzSheet *sheet, uint64_t *evaluated)
{
    KhzArena *arena;
    size_t *order;
    size_t mark;
    size_t capacity;
    size_t count = (size_t)0;
    uint64_t done = (uint64_t)0;
    KhzSheetStatus status;
    size_t i;

    if (sheet == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    if (evaluated != NULL) {
        *evaluated = (uint64_t)0;
    }

    arena = khz_sheet_arena(sheet);
    if (arena == NULL) {
        return KHZ_SHEET_ERR_STATE;
    }

    capacity = khz_sheet_cell_count(sheet);
    if (capacity == (size_t)0) {
        return KHZ_SHEET_OK;
    }

    status = khz_dep_range_sync(&sheet->deps);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    capacity = khz_sheet_cell_count(sheet);
    mark = khz_arena_mark(arena);

    order = (size_t *)khz_arena_alloc(arena, capacity * sizeof(size_t));
    if (order == NULL) {
        (void)khz_arena_release(arena, mark);
        return KHZ_SHEET_ERR_MEMORY;
    }

    status = khz_formula_evaluation_order(sheet, order, capacity, &count);
    if (status != KHZ_SHEET_OK) {
        (void)khz_arena_release(arena, mark);
        return status;
    }

    for (i = (size_t)0; i < count; ++i) {
        KhzCell *cell;
        KhzFormula formula;
        KhzFormulaResult result;
        int changed;

        if (order[i] >= sheet->grid.cell_count) {
            (void)khz_arena_release(arena, mark);
            return KHZ_SHEET_ERR_STATE;
        }

        cell = &sheet->grid.cells[order[i]];

        if (cell->kind != (uint32_t)KHZ_CELL_FORMULA || cell->formula == NULL) {
            continue;
        }

        if ((cell->flags & (uint32_t)KHZ_CELL_FLAG_DIRTY) == 0u) {
            continue;
        }

        status = khz_formula_parse(&formula, arena, cell->col, cell->row,
                                   cell->formula, (size_t)cell->formula_len, NULL);
        if (status != KHZ_SHEET_OK) {
            (void)khz_arena_release(arena, mark);
            return status;
        }

        status = khz_formula_eval(sheet, &formula, &result);
        khz_formula_abandon(&formula);

        if (status != KHZ_SHEET_OK) {
            (void)khz_arena_release(arena, mark);
            return status;
        }

        changed = khz_value_changed(cell, &result);

        if (khz_deps_result_is_error(&result)) {
            cell->error = result.error;
            status = khz_rational_make((int64_t)0, (int64_t)1, &cell->value);
        } else {
            cell->error = (uint32_t)KHZ_CELL_ERROR_NONE;
            cell->value = result.value;
            status = KHZ_SHEET_OK;
        }

        if (status != KHZ_SHEET_OK) {
            (void)khz_arena_release(arena, mark);
            return status;
        }

        cell->flags &= ~(uint32_t)KHZ_CELL_FLAG_DIRTY;

        status = khz_sheet_commit_in_place(sheet, cell);
        if (status != KHZ_SHEET_OK) {
            (void)khz_arena_release(arena, mark);
            return status;
        }

        if (changed) {
            khz_dirty_dependents(sheet, order[i]);
        }

        ++done;
    }

    (void)khz_arena_release(arena, mark);

    if (evaluated != NULL) {
        *evaluated = done;
    }

    return KHZ_SHEET_OK;
}
