/* Evaluation half of the formula module.

   The module is spread over three translation units because one file per
   commit is the push budget this repository is built under, not because the
   halves are independent. They share no state beyond the public header.

     khz_formula.c        IR builder and recursive-descent parser
     khz_formula_eval.c   this file: evaluating a tree to a value
     khz_formula_deps.c   declaring dependencies, setting, recalculating

   The third file was split off in Phase 96. Rewriting a 29 KB file to change
   twenty lines of it means re-transmitting all 29 KB, and a truncated push has
   already committed a broken file in this repository once. */

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

/* ---------------------------------------------------------------- *
 * Power
 *
 * Square and multiply over khz_rational_mul. Every partial product is reduced
 * by the same routine that reduces a multiplication typed by the user, and an
 * overflow anywhere in the chain is refused rather than wrapped or saturated,
 * so 2^62 is exact and 2^63 is #NUM!.
 *
 * The exponent is required to be an integer. A non-integer exponent is #NUM!
 * in the cell, which means khz_formula_eval_strict reports it as
 * KHZ_SHEET_ERR_OVERFLOW - not the KHZ_SHEET_ERR_UNSUPPORTED that the header
 * claimed when the op was declared. There is no cell error meaning
 * "unsupported" for a value to carry, and making it a status instead would
 * let one cell holding =2^0.5 abort recalculation of the whole sheet. Overflow
 * is also the honest description: an irrational result is exactly what an
 * int64 rational cannot represent.
 * ---------------------------------------------------------------- */

static KhzSheetStatus khz_pow_magnitude(KhzRational base, int64_t exponent,
                                        KhzRational *out)
{
    KhzRational result;
    KhzRational factor = base;
    int64_t n = exponent; /* caller guarantees 1 <= n <= KHZ_FORMULA_MAX_EXPONENT */
    KhzSheetStatus status = khz_rational_make((int64_t)1, (int64_t)1, &result);

    if (status != KHZ_SHEET_OK) {
        return status;
    }

    while (n > (int64_t)0) {
        if ((n & (int64_t)1) != (int64_t)0) {
            status = khz_rational_mul(result, factor, &result);
            if (status != KHZ_SHEET_OK) {
                return status;
            }
        }

        n >>= 1;

        /* Guarded, so the last iteration does not square a factor nobody will
           read. Without the guard 2^62 would compute 2^124 on the way past
           and refuse a result that fits. */
        if (n > (int64_t)0) {
            status = khz_rational_mul(factor, factor, &factor);
            if (status != KHZ_SHEET_OK) {
                return status;
            }
        }
    }

    *out = result;
    return KHZ_SHEET_OK;
}

static KhzSheetStatus khz_eval_pow(KhzRational base, KhzRational exponent,
                                   KhzFormulaResult *out)
{
    KhzRational one;
    KhzRational value;
    KhzSheetStatus status;
    int64_t n;

    if (exponent.den != (int64_t)1) {
        return khz_result_error(out, KHZ_CELL_ERROR_NUM);
    }

    n = exponent.num;

    status = khz_rational_make((int64_t)1, (int64_t)1, &one);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    /* 0^0 is 1 because Excel says 1, not because the mathematics is settled. */
    if (n == (int64_t)0) {
        return khz_result_value(out, one);
    }

    if (base.num == (int64_t)0) {
        if (n < (int64_t)0) {
            return khz_result_error(out, KHZ_CELL_ERROR_DIV0);
        }
        return khz_result_zero(out);
    }

    if (base.num == (int64_t)1 && base.den == (int64_t)1) {
        return khz_result_value(out, one);
    }

    if (base.num == (int64_t)-1 && base.den == (int64_t)1) {
        if ((n % (int64_t)2) == (int64_t)0) {
            return khz_result_value(out, one);
        }
        return khz_result_value(out, base);
    }

    /* Compared before negating, so an exponent of INT64_MIN is rejected here
       rather than negated into itself. Every base that reaches this point has
       magnitude at least 2 or at most 1/2 and so leaves int64 long before the
       bound, which is why the bound refuses nothing that could have
       succeeded. */
    if (n > KHZ_FORMULA_MAX_EXPONENT || n < -KHZ_FORMULA_MAX_EXPONENT) {
        return khz_result_error(out, KHZ_CELL_ERROR_NUM);
    }

    status = khz_pow_magnitude(base, n < (int64_t)0 ? -n : n, &value);

    if (status == KHZ_SHEET_OK && n < (int64_t)0) {
        /* Reciprocal of the finished power, not a division repeated inside the
           loop: 2^-3 is 1/8 exactly. value.num cannot be zero, because a base
           with a zero numerator was answered above. */
        status = khz_rational_div(one, value, &value);
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
    case KHZ_FORMULA_POW:
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

        if (node->op == (uint32_t)KHZ_FORMULA_POW) {
            return khz_eval_pow(left.value, right.value, out);
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
