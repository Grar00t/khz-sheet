/* Dependency declaration, formula setting and recalculation.

   Split out of src/khz_formula_eval.c in Phase 96. See the header comment
   there for why the module spans three translation units.

   This is the file that Phase 96 exists to change. Every earlier phase
   lowered a range reference into one concrete edge per cell that happened to
   exist when the formula was declared, and the TODO block that used to sit in
   khz_declare_walk admitted the consequence: =SUM(A1:A100) never noticed a
   value typed into A50 afterwards. It now declares the rectangle itself and
   lets khz_dep_range_sync link cells as they appear. */

#include "khz_formula.h"

#include <string.h>

#include "khz_sheet.h"

/* Duplicated rather than shared. It is three tokens, and exporting a helper
   this small across translation units would put a function call in the public
   namespace purely to avoid retyping a comparison. */
static int khz_deps_result_is_error(const KhzFormulaResult *r)
{
    return r->kind == (uint32_t)KHZ_CELL_ERROR;
}

/* ---------------------------------------------------------------- *
 * Dependency declaration
 * ---------------------------------------------------------------- */

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
        /* A single cell reference is still a single edge. There is no region
           to re-scan, so routing it through the range machinery would add a
           permanent list node and a rescan watermark for no benefit. */
        status = khz_sheet_declare_dependency(sheet, node->col0, node->row0, col, row);
        if (status != KHZ_SHEET_OK) {
            return status;
        }
        *declared += (uint64_t)1;
        return KHZ_SHEET_OK;
    }

    if (node->op == (uint32_t)KHZ_FORMULA_RANGE) {
        /* One region, declared once, regardless of how many cells are inside
           it now or later. The rectangle is stored; membership is decided at
           recalculation time by khz_dep_range_sync, which khz_dep_topo calls.

           A self-reference is not filtered out here. If this formula sits
           inside the region it reads, that is a circular reference, and the
           graph reports KHZ_SHEET_ERR_CYCLE. Skipping the self-edge - which
           is what the old per-cell loop did - broke the cycle silently and
           produced a number that looked like an answer.

           declared counts regions, not cells, so the number it returns is no
           longer comparable with what earlier phases reported for the same
           formula. That is the honest count: one dependency was declared. */
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

/* ---------------------------------------------------------------- *
 * Setting and recalculating
 * ---------------------------------------------------------------- */

KhzSheetStatus khz_formula_set(KhzSheet *sheet, uint32_t col, uint32_t row,
                               const char *source, size_t len,
                               KhzFormulaParseError *error)
{
    KhzArena *arena;
    KhzFormula formula;
    KhzSheetStatus status;

    if (sheet == NULL || source == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    arena = khz_sheet_arena(sheet);
    if (arena == NULL) {
        return KHZ_SHEET_ERR_STATE;
    }

    /* Parse before touching the sheet. A formula that does not parse must leave
       no cell, no edge and no chain entry behind. */
    status = khz_formula_parse(&formula, arena, col, row, source, len, error);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    status = khz_formula_declare_dependencies(sheet, &formula, NULL);

    /* The tree is scratch: the cell stores the source text and recalculation
       reparses it. Releasing here returns every node to the arena before the
       text is copied in below, so a sheet of formulas costs source bytes and
       not tree bytes.

       Note that a range edge is not scratch and outlives this release: it is
       allocated permanently by khz_dep_add_range_edge, exactly like the
       concrete edges a REF declares, and is not part of the formula tree. */
    khz_formula_abandon(&formula);

    if (status != KHZ_SHEET_OK) {
        return status;
    }

    /* khz_sheet_set_formula marks the new cell dirty, so the next recalc
       picks it up without a full sweep being needed to find it. */
    return khz_sheet_set_formula(sheet, col, row, source, len);
}

/* Marks the direct dependents of a cell as needing recalculation.

   Only direct dependents: recalculation walks topological order, so a cell
   marked here is always visited later in the same pass, and when its own value
   changes it marks its dependents in turn. Transitivity comes from the order,
   not from a second traversal.

   This reads graph->heads, so it only sees regions that have already been
   materialised into concrete edges. That is why khz_dep_range_scan marks the
   dependent dirty itself at the moment it creates a link: a cell written into
   a region before the next sync would otherwise be invisible here. */
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

/* Recalculates the formula cells that are marked dirty, in topological order.

   Phase 93 recomputed and re-committed every formula cell on the sheet on
   every call. That was not merely slow: each commit forms a link in the proof
   chain, so a sweep of a thousand untouched formulas appended a thousand
   chain entries recording that nothing had changed. The chain is meant to be
   a record of edits, and a sweep filled it with non-edits.

   So a cell is recomputed only when its dirty flag is set, and the flag
   spreads along the dependency graph as values actually change. A cell whose
   recomputed value is identical to the one it already held is committed but
   does not dirty its dependents, which stops a no-op edit from cascading. */
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

    mark = khz_arena_mark(arena);

    order = (size_t *)khz_arena_alloc(arena, capacity * sizeof(size_t));
    if (order == NULL) {
        (void)khz_arena_release(arena, mark);
        return KHZ_SHEET_ERR_MEMORY;
    }

    /* This is also where declared regions are resolved: khz_sheet_evaluation_order
       calls khz_dep_topo, which syncs range edges before it reads indegree.
       Any cell created inside a declared region since the last recalc is
       linked and marked dirty by that sync, so it is both ordered correctly
       and actually recomputed on this call rather than a later one.

       KHZ_SHEET_ERR_CYCLE propagates unchanged. A circular reference is not
       evaluated partially and is not reported as a value; nothing is written. */
    status = khz_sheet_evaluation_order(sheet, order, capacity, &count);
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

        /* Clean cells are skipped. Their cached value is current by
           construction: it was computed after the last change to anything
           they read, which is exactly what clearing the flag recorded. */
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

        /* Written in place through grid.cells[], keeping kind FORMULA and the
           source text. The setters cannot be used here: khz_sheet_set_rational
           would replace the formula with its own result and the sheet would
           forget how the number was produced. */
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

        /* One commit path for the whole system. This used to write
           sheet->proof and bump the counter directly, duplicating what the
           setters do; since Phase 94 the sheet also records every commit in
           its log, and a link formed without an entry would leave
           khz_sheet_audit_chain unable to account for the head it found. */
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
