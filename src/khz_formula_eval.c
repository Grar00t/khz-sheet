/* Evaluation half of the formula module. Split from src/khz_formula.c only
   because one file per commit is the push budget this repository is built
   under; the two translation units are one module and share no state beyond
   the public header. */

#include "khz_formula.h"

#include <string.h>

#include "khz_sheet.h"
#include "khz_simd.h"

/* ---------------------------------------------------------------- *
 * Status and cell-error correspondence
 * ---------------------------------------------------------------- */

KhzCellError khz_formula_status_to_error(KhzSheetStatus status)
{
    switch (status) {
    case KHZ_SHEET_ERR_DIVZERO:  return KHZ_CELL_ERROR_DIV0;
    case KHZ_SHEET_ERR_TYPE:     return KHZ_CELL_ERROR_VALUE;
    case KHZ_SHEET_ERR_OVERFLOW: return KHZ_CELL_ERROR_NUM;
    case KHZ_SHEET_ERR_RANGE:    return KHZ_CELL_ERROR_NUM;
    case KHZ_SHEET_ERR_MISSING:  return KHZ_CELL_ERROR_REF;
    default:                     return KHZ_CELL_ERROR_VALUE;
    }
}

KhzSheetStatus khz_formula_error_to_status(KhzCellError error)
{
    switch (error) {
    case KHZ_CELL_ERROR_NONE:  return KHZ_SHEET_OK;
    case KHZ_CELL_ERROR_DIV0:  return KHZ_SHEET_ERR_DIVZERO;
    case KHZ_CELL_ERROR_VALUE: return KHZ_SHEET_ERR_TYPE;
    case KHZ_CELL_ERROR_NUM:   return KHZ_SHEET_ERR_OVERFLOW;
    case KHZ_CELL_ERROR_REF:   return KHZ_SHEET_ERR_MISSING;
    default:                   return KHZ_SHEET_ERR_TYPE;
    }
}

static KhzSheetStatus khz_result_zero(KhzFormulaResult *out)
{
    out->kind = (uint32_t)KHZ_CELL_RATIONAL;
    out->error = (uint32_t)KHZ_CELL_ERROR_NONE;
    return khz_rational_make((int64_t)0, (int64_t)1, &out->value);
}

static KhzSheetStatus khz_result_value(KhzFormulaResult *out, KhzRational value)
{
    out->kind = (uint32_t)KHZ_CELL_RATIONAL;
    out->error = (uint32_t)KHZ_CELL_ERROR_NONE;
    out->value = value;
    return KHZ_SHEET_OK;
}

/* A spreadsheet-representable failure becomes a value, not a return code. The
   distinction this module keeps is: structural problems (null, cycle, out of
   memory, malformed tree) are statuses the caller must handle, while #DIV/0!
   and #VALUE! are results a cell can hold and a dependent formula can
   propagate. khz_formula_eval_strict exists for callers who want the raw
   status instead. */
static KhzSheetStatus khz_result_error(KhzFormulaResult *out, KhzCellError error)
{
    out->kind = (uint32_t)KHZ_CELL_ERROR;
    out->error = (uint32_t)error;
    return khz_rational_make((int64_t)0, (int64_t)1, &out->value);
}

static int khz_result_is_error(const KhzFormulaResult *r)
{
    return r->kind == (uint32_t)KHZ_CELL_ERROR;
}

/* ---------------------------------------------------------------- *
 * Range membership
 * ---------------------------------------------------------------- */

static int khz_cell_in_node(const KhzCell *cell, const KhzFormulaNode *node)
{
    return cell->col >= node->col0 && cell->col <= node->col1
        && cell->row >= node->row0 && cell->row <= node->row1;
}

/* Ranges are walked over the cells that exist, not over the coordinates the
   rectangle spans. A1:XFD1048576 names 17 billion coordinates and a sheet
   holds at most 2^26 cells; iterating the rectangle would hang on a range a
   user can type in two seconds. */
static size_t khz_range_population(const KhzSheet *sheet, const KhzFormulaNode *node)
{
    size_t total = (size_t)0;
    size_t i;

    for (i = (size_t)0; i < sheet->grid.cell_count; ++i) {
        const KhzCell *cell = &sheet->grid.cells[i];

        if (!khz_cell_in_node(cell, node)) {
            continue;
        }
        if (cell->kind == (uint32_t)KHZ_CELL_RATIONAL
            || cell->kind == (uint32_t)KHZ_CELL_FORMULA
            || cell->kind == (uint32_t)KHZ_CELL_ERROR) {
            ++total;
        }
    }

    return total;
}

/* ---------------------------------------------------------------- *
 * Evaluation
 * ---------------------------------------------------------------- */

static KhzSheetStatus khz_eval_node(KhzSheet *sheet, const KhzFormulaNode *node,
                                    size_t depth, KhzFormulaResult *out);

static KhzSheetStatus khz_eval_ref(const KhzSheet *sheet, const KhzFormulaNode *node,
                                   KhzFormulaResult *out)
{
    const KhzCell *cell = NULL;
    KhzSheetStatus status = khz_sheet_get(sheet, node->col0, node->row0, &cell);

    /* A blank cell reads as zero in arithmetic. That is the spreadsheet rule,
       and it is why a missing coordinate is not an error here. */
    if (status == KHZ_SHEET_ERR_MISSING) {
        return khz_result_zero(out);
    }
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    switch ((KhzCellKind)cell->kind) {
    case KHZ_CELL_EMPTY:
        return khz_result_zero(out);
    case KHZ_CELL_RATIONAL:
        return khz_result_value(out, cell->value);
    case KHZ_CELL_FORMULA:
        /* The cached value. Recalculation visits cells in topological order,
           so by the time a dependent is evaluated this cache is current. Off
           the recalc path it may be stale, which is what the dirty flag
           records. */
        if ((cell->error != (uint32_t)KHZ_CELL_ERROR_NONE)) {
            return khz_result_error(out, (KhzCellError)cell->error);
        }
        return khz_result_value(out, cell->value);
    case KHZ_CELL_BOOL:
        return khz_result_value(out,
                                (KhzRational){ cell->bool_value ? (int64_t)1 : (int64_t)0,
                                               (int64_t)1 });
    case KHZ_CELL_ERROR:
        return khz_result_error(out, (KhzCellError)cell->error);
    case KHZ_CELL_TEXT:
    default:
        /* Text in arithmetic is #VALUE!. No coercion is attempted: guessing a
           number out of a string is how a spreadsheet silently lies. */
        return khz_result_error(out, KHZ_CELL_ERROR_VALUE);
    }
}

typedef struct KhzAgg {
    KhzRational *values;
    size_t       count;
    size_t       capacity;
} KhzAgg;

static KhzSheetStatus khz_agg_push(KhzAgg *agg, KhzRational value)
{
    if (agg->count >= agg->capacity) {
        return KHZ_SHEET_ERR_LIMIT;
    }

    agg->values[agg->count] = value;
    agg->count += (size_t)1;

    return KHZ_SHEET_OK;
}

/* Collects the operands of an aggregate. Returns OK with *failed set when a
   contributing cell holds an error, so the caller can propagate that error
   rather than average over it. */
static KhzSheetStatus khz_agg_gather(KhzSheet *sheet, const KhzFormulaNode *node,
                                     size_t depth, KhzAgg *agg,
                                     KhzCellError *failed)
{
    uint32_t i;

    for (i = 0u; i < node->child_count; ++i) {
        const KhzFormulaNode *child = node->children[i];

        if (child->op == (uint32_t)KHZ_FORMULA_RANGE) {
            size_t j;

            for (j = (size_t)0; j < sheet->grid.cell_count; ++j) {
                const KhzCell *cell = &sheet->grid.cells[j];
                KhzSheetStatus status;

                if (!khz_cell_in_node(cell, child)) {
                    continue;
                }

                if (cell->kind == (uint32_t)KHZ_CELL_ERROR
                    || (cell->kind == (uint32_t)KHZ_CELL_FORMULA
                        && cell->error != (uint32_t)KHZ_CELL_ERROR_NONE)) {
                    *failed = (KhzCellError)cell->error;
                    return KHZ_SHEET_OK;
                }

                /* Text and blanks inside a range are skipped, not zeroed.
                   AVERAGE over two numbers and a label is the mean of two. */
                if (cell->kind != (uint32_t)KHZ_CELL_RATIONAL
                    && cell->kind != (uint32_t)KHZ_CELL_FORMULA) {
                    continue;
                }

                status = khz_agg_push(agg, cell->value);
                if (status != KHZ_SHEET_OK) {
                    return status;
                }
            }
        } else {
            KhzFormulaResult scalar;
            KhzSheetStatus status = khz_eval_node(sheet, child, depth, &scalar);

            if (status != KHZ_SHEET_OK) {
                return status;
            }
            if (khz_result_is_error(&scalar)) {
                *failed = (KhzCellError)scalar.error;
                return KHZ_SHEET_OK;
            }

            status = khz_agg_push(agg, scalar.value);
            if (status != KHZ_SHEET_OK) {
                return status;
            }
        }
    }

    return KHZ_SHEET_OK;
}

/* Integer operands take the vector path. This is the only reason
   khz_simd_min_i64 and khz_simd_max_i64 exist, so the check is not an
   optimisation detail - without it the new kernels would be dead code. */
static int khz_agg_all_integer(const KhzAgg *agg)
{
    size_t i;

    for (i = (size_t)0; i < agg->count; ++i) {
        if (agg->values[i].den != (int64_t)1) {
            return 0;
        }
    }

    return agg->count > (size_t)0;
}

static KhzSheetStatus khz_agg_integer_lane(KhzSheet *sheet, const KhzAgg *agg,
                                           KhzFormulaOp op, KhzRational *out,
                                           int *handled)
{
    KhzArena *arena = khz_sheet_arena(sheet);
    size_t mark;
    int64_t *lane;
    int64_t result = (int64_t)0;
    KhzSheetStatus status;
    size_t i;

    *handled = 0;

    if (!khz_agg_all_integer(agg)) {
        return KHZ_SHEET_OK;
    }
    if (op == KHZ_FORMULA_AVG) {
        /* The mean of integers is not an integer. It stays on the rational
           path so 1 and 2 average to 3/2. */
        return KHZ_SHEET_OK;
    }

    mark = khz_arena_mark(arena);

    lane = (int64_t *)khz_arena_alloc(arena, agg->count * sizeof(int64_t));
    if (lane == NULL) {
        (void)khz_arena_release(arena, mark);
        return KHZ_SHEET_ERR_MEMORY;
    }

    for (i = (size_t)0; i < agg->count; ++i) {
        lane[i] = agg->values[i].num;
    }

    if (op == KHZ_FORMULA_SUM) {
        status = khz_simd_sum_i64(lane, agg->count, &result);
    } else if (op == KHZ_FORMULA_MIN) {
        status = khz_simd_min_i64(lane, agg->count, &result);
    } else {
        status = khz_simd_max_i64(lane, agg->count, &result);
    }

    if (status == KHZ_SHEET_OK) {
        status = khz_rational_make(result, (int64_t)1, out);
    }

    (void)khz_arena_release(arena, mark);

    if (status == KHZ_SHEET_OK) {
        *handled = 1;
    }

    return status;
}

static KhzSheetStatus khz_eval_aggregate(KhzSheet *sheet, const KhzFormulaNode *node,
                                         size_t depth, KhzFormulaResult *out)
{
    KhzArena *arena = khz_sheet_arena(sheet);
    size_t mark = khz_arena_mark(arena);
    KhzCellError failed = KHZ_CELL_ERROR_NONE;
    KhzAgg agg;
    KhzRational value;
    KhzSheetStatus status;
    size_t capacity = (size_t)0;
    int handled = 0;
    uint32_t i;

    for (i = 0u; i < node->child_count; ++i) {
        const KhzFormulaNode *child = node->children[i];

        if (child->op == (uint32_t)KHZ_FORMULA_RANGE) {
            capacity += khz_range_population(sheet, child);
        } else {
            capacity += (size_t)1;
        }
    }

    memset(&agg, 0, sizeof agg);
    agg.capacity = capacity;

    if (capacity > (size_t)0) {
        agg.values = (KhzRational *)khz_arena_alloc(arena,
                                                    capacity * sizeof(KhzRational));
        if (agg.values == NULL) {
            (void)khz_arena_release(arena, mark);
            return KHZ_SHEET_ERR_MEMORY;
        }
    }

    status = khz_agg_gather(sheet, node, depth, &agg, &failed);
    if (status != KHZ_SHEET_OK) {
        (void)khz_arena_release(arena, mark);
        return status;
    }

    if (failed != KHZ_CELL_ERROR_NONE) {
        (void)khz_arena_release(arena, mark);
        return khz_result_error(out, failed);
    }

    if (agg.count == (size_t)0) {
        (void)khz_arena_release(arena, mark);

        /* SUM of nothing is 0 and MIN of nothing is 0, matching what a
           spreadsheet shows for an empty range. AVERAGE of nothing is
           #DIV/0!, because the divisor really is zero. */
        if (node->op == (uint32_t)KHZ_FORMULA_AVG) {
            return khz_result_error(out, KHZ_CELL_ERROR_DIV0);
        }
        return khz_result_zero(out);
    }

    status = khz_agg_integer_lane(sheet, &agg, (KhzFormulaOp)node->op,
                                  &value, &handled);

    if (status == KHZ_SHEET_OK && !handled) {
        switch ((KhzFormulaOp)node->op) {
        case KHZ_FORMULA_SUM:
            status = khz_simd_sum_rational(agg.values, agg.count, &value);
            break;
        case KHZ_FORMULA_AVG:
            status = khz_simd_avg_rational(agg.values, agg.count, &value);
            break;
        case KHZ_FORMULA_MIN:
            status = khz_simd_min_rational(agg.values, agg.count, &value);
            break;
        default:
            status = khz_simd_max_rational(agg.values, agg.count, &value);
            break;
        }
    }

    (void)khz_arena_release(arena, mark);

    if (status == KHZ_SHEET_ERR_OVERFLOW) {
        return khz_result_error(out, KHZ_CELL_ERROR_NUM);
    }
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    return khz_result_value(out, value);
}

static KhzSheetStatus khz_eval_arith(const KhzFormulaNode *node,
                                     KhzRational a, KhzRational b,
                                     KhzFormulaResult *out)
{
    KhzRational value;
    KhzSheetStatus status;

    switch ((KhzFormulaOp)node->op) {
    case KHZ_FORMULA_ADD:
        status = khz_rational_add(a, b, &value);
        break;
    case KHZ_FORMULA_SUB:
        status = khz_rational_sub(a, b, &value);
        break;
    case KHZ_FORMULA_MUL:
        status = khz_rational_mul(a, b, &value);
        break;
    default:
        if (b.num == (int64_t)0) {
            return khz_result_error(out, KHZ_CELL_ERROR_DIV0);
        }
        status = khz_rational_div(a, b, &value);
        break;
    }

    if (status == KHZ_SHEET_ERR_OVERFLOW) {
        return khz_result_error(out, KHZ_CELL_ERROR_NUM);
    }
    if (status == KHZ_SHEET_ERR_DIVZERO) {
        return khz_result_error(out, KHZ_CELL_ERROR_DIV0);
    }
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    return khz_result_value(out, value);
}

static KhzSheetStatus khz_eval_node(KhzSheet *sheet, const KhzFormulaNode *node,
                                    size_t depth, KhzFormulaResult *out)
{
    KhzFormulaResult left;
    KhzFormulaResult right;
    KhzRational zero;
    KhzSheetStatus status;

    if (sheet == NULL || node == NULL || out == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }
    if (depth > KHZ_FORMULA_MAX_DEPTH) {
        return KHZ_SHEET_ERR_LIMIT;
    }

    switch ((KhzFormulaOp)node->op) {
    case KHZ_FORMULA_CONST:
        return khz_result_value(out, node->value);

    case KHZ_FORMULA_REF:
        return khz_eval_ref(sheet, node, out);

    case KHZ_FORMULA_RANGE:
        /* A rectangle is not a number. A1:B2 outside an aggregate is #VALUE!,
           not the first cell and not the last. */
        return khz_result_error(out, KHZ_CELL_ERROR_VALUE);

    case KHZ_FORMULA_SUM:
    case KHZ_FORMULA_AVG:
    case KHZ_FORMULA_MIN:
    case KHZ_FORMULA_MAX:
        return khz_eval_aggregate(sheet, node, depth + (size_t)1, out);

    case KHZ_FORMULA_NEG:
        if (node->child_count != 1u) {
            return KHZ_SHEET_ERR_FORMAT;
        }

        status = khz_eval_node(sheet, node->children[0], depth + (size_t)1, &left);
        if (status != KHZ_SHEET_OK) {
            return status;
        }
        if (khz_result_is_error(&left)) {
            return khz_result_error(out, (KhzCellError)left.error);
        }

        status = khz_rational_make((int64_t)0, (int64_t)1, &zero);
        if (status != KHZ_SHEET_OK) {
            return status;
        }

        /* 0 - x rather than a dedicated negate, so the one overflow case
           (num == INT64_MIN) is refused by the same code path as every other
           subtraction instead of by a special case that could disagree. */
        status = khz_rational_sub(zero, left.value, &zero);
        if (status == KHZ_SHEET_ERR_OVERFLOW) {
            return khz_result_error(out, KHZ_CELL_ERROR_NUM);
        }
        if (status != KHZ_SHEET_OK) {
            return status;
        }

        return khz_result_value(out, zero);

    case KHZ_FORMULA_ADD:
    case KHZ_FORMULA_SUB:
    case KHZ_FORMULA_MUL:
    case KHZ_FORMULA_DIV:
        if (node->child_count != 2u) {
            return KHZ_SHEET_ERR_FORMAT;
        }

        status = khz_eval_node(sheet, node->children[0], depth + (size_t)1, &left);
        if (status != KHZ_SHEET_OK) {
            return status;
        }
        status = khz_eval_node(sheet, node->children[1], depth + (size_t)1, &right);
        if (status != KHZ_SHEET_OK) {
            return status;
        }

        /* Leftmost error wins, which is the order a reader expects. */
        if (khz_result_is_error(&left)) {
            return khz_result_error(out, (KhzCellError)left.error);
        }
        if (khz_result_is_error(&right)) {
            return khz_result_error(out, (KhzCellError)right.error);
        }

        return khz_eval_arith(node, left.value, right.value, out);

    default:
        return KHZ_SHEET_ERR_UNSUPPORTED;
    }
}

KhzSheetStatus khz_formula_eval(KhzSheet *sheet, const KhzFormula *formula,
                                KhzFormulaResult *out)
{
    if (sheet == NULL || formula == NULL || out == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }
    if (formula->root == NULL) {
        return KHZ_SHEET_ERR_STATE;
    }

    memset(out, 0, sizeof *out);

    return khz_eval_node(sheet, formula->root, (size_t)0, out);
}

KhzSheetStatus khz_formula_eval_strict(KhzSheet *sheet, const KhzFormula *formula,
                                       KhzRational *out)
{
    KhzFormulaResult result;
    KhzSheetStatus status;

    if (out == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    status = khz_formula_eval(sheet, formula, &result);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    if (khz_result_is_error(&result)) {
        return khz_formula_error_to_status((KhzCellError)result.error);
    }

    *out = result.value;
    return KHZ_SHEET_OK;
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
        status = khz_sheet_declare_dependency(sheet, node->col0, node->row0, col, row);
        if (status != KHZ_SHEET_OK) {
            return status;
        }
        *declared += (uint64_t)1;
        return KHZ_SHEET_OK;
    }

    if (node->op == (uint32_t)KHZ_FORMULA_RANGE) {
        size_t j;

        /* TODO (range edges are not retroactive).

           One edge per cell that exists in the rectangle at declaration time.
           A cell created inside the range afterwards is never linked, so
           =SUM(A1:A100) will not notice a value later typed into A50 unless
           the formula is set again.

           Not fixed in Phase 94, and deliberately not papered over with a
           workaround: the honest fix is a range-edge form in KhzDepGraph, so
           an edge names a rectangle rather than a list of cells and
           membership is tested at recalculation time instead of frozen at
           declaration time. That changes khz_dep_add_edge, khz_dep_topo and
           the indegree accounting together, which is a graph change rather
           than an evaluator change and does not belong in this file. */
        for (j = (size_t)0; j < sheet->grid.cell_count; ++j) {
            const KhzCell *cell = &sheet->grid.cells[j];

            if (!khz_cell_in_node(cell, node)) {
                continue;
            }
            if (cell->col == col && cell->row == row) {
                continue;
            }

            status = khz_sheet_declare_dependency(sheet, cell->col, cell->row, col, row);
            if (status != KHZ_SHEET_OK) {
                return status;
            }
            *declared += (uint64_t)1;
        }

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
       not tree bytes. */
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
   not from a second traversal. */
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
    if (khz_result_is_error(result)) {
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

    /* KHZ_SHEET_ERR_CYCLE propagates unchanged. A circular reference is not
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
        if (khz_result_is_error(&result)) {
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
