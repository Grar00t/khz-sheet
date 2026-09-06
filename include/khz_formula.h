#ifndef KHZ_FORMULA_H
#define KHZ_FORMULA_H

#include <stddef.h>
#include <stdint.h>

#include "khz_arena.h"
#include "khz_cell.h"
#include "khz_grid.h"
#include "khz_rational.h"
#include "khz_status.h"

#ifdef __cplusplus
extern "C" {
#endif

struct KhzSheet;

/* ---------------------------------------------------------------------- *
 * THE IR BOUNDARY DECISION
 *
 * Option A was chosen: C owns the intermediate representation.
 *
 * The alternative was to keep evaluation in managed code and let C serve only
 * SIMD kernels. That option is rejected for a concrete reason, not a slogan.
 * Every value in this engine is an exact int64 rational, and the invariants
 * that make it exact - den > 0, gcd reduced, INT64_MIN excluded, overflow
 * refused rather than wrapped - live in khz_rational.c. An evaluator written
 * in C# would either re-implement those invariants, giving two arithmetics
 * that must be kept in agreement forever, or marshal every intermediate value
 * across the boundary, which costs more than the kernels save. Neither is
 * acceptable.
 *
 * There is also a correctness argument that decides it outright: the proof
 * chain is computed in C over the committed cell bytes. If a value were
 * produced managed-side and then handed down to be committed, the digest would
 * attest to a number the C layer never computed. Evaluation must happen below
 * the hashing boundary or the chain proves nothing about the arithmetic.
 *
 * So FormulaNode.cs is a PARSE TREE, not the IR. C# lowers it into the nodes
 * declared here through the builder API, or hands the source text to
 * khz_formula_parse and skips its own parser entirely. Both routes end in the
 * same KhzFormulaNode graph and the same evaluator. FormulaNode.cs is not
 * marshalled directly and never will be: a managed object graph has no stable
 * layout to marshal.
 * ---------------------------------------------------------------------- */

typedef enum KhzFormulaOp {
    KHZ_FORMULA_CONST = 0,  /* literal rational */
    KHZ_FORMULA_REF   = 1,  /* single cell */
    KHZ_FORMULA_RANGE = 2,  /* inclusive rectangle, aggregate arguments only */
    KHZ_FORMULA_ADD   = 3,
    KHZ_FORMULA_SUB   = 4,
    KHZ_FORMULA_MUL   = 5,
    KHZ_FORMULA_DIV   = 6,
    KHZ_FORMULA_NEG   = 7,  /* unary minus */
    KHZ_FORMULA_SUM   = 8,
    KHZ_FORMULA_AVG   = 9,
    KHZ_FORMULA_MIN   = 10,
    KHZ_FORMULA_MAX   = 11
} KhzFormulaOp;

/* RANGE and NEG are additions to the ten operations that were specified.
   Neither is optional: SUM(A1:B9) has no meaning without a range node, and
   without NEG the expression -A1 has to be faked as 0-A1, which is a different
   tree and would silently change what the proof chain attests to. */

#define KHZ_FORMULA_MAX_DEPTH   ((size_t)64)
#define KHZ_FORMULA_MAX_NODES   ((size_t)4096)
#define KHZ_FORMULA_MAX_ARGS    ((size_t)64)
#define KHZ_FORMULA_MAX_SOURCE  ((size_t)8192)

/* One node. Children are an arena array of pointers, so an n-ary aggregate and
   a binary operator use the same shape and the evaluator has one walk.

   Nothing here is reference counted and nothing is freed individually. The
   whole tree belongs to the arena it was built from and dies with it, like
   every other allocation below this line. */
typedef struct KhzFormulaNode {
    uint32_t                op;          /* KhzFormulaOp */
    uint32_t                child_count;
    struct KhzFormulaNode **children;

    KhzRational             value;       /* CONST */

    /* REF uses col0/row0. RANGE uses all four, normalised so col0 <= col1 and
       row0 <= row1 at construction, because A3:C1 and C1:A3 are the same
       rectangle and must produce the same tree and the same digest. */
    uint32_t                col0;
    uint32_t                row0;
    uint32_t                col1;
    uint32_t                row1;
} KhzFormulaNode;

/* A parsed or built formula bound to the cell that owns it. */
typedef struct KhzFormula {
    KhzArena       *arena;
    KhzFormulaNode *root;
    uint32_t        col;
    uint32_t        row;
    uint64_t        node_count;
    uint64_t        ref_count;   /* REF plus RANGE nodes, before expansion */
    size_t          mark;        /* arena offset at begin, for release */
} KhzFormula;

/* Where a parse failed, in bytes from the start of the source. Reported so a
   caller can point at the offending character instead of saying "bad
   formula". */
typedef struct KhzFormulaParseError {
    size_t offset;
    char   expected[32];
} KhzFormulaParseError;

/* Result of evaluating a tree.
 *
 * Two kinds of failure exist here and they are not the same thing:
 *
 *   - A SPREADSHEET ERROR is a value. Dividing by zero yields #DIV/0! in the
 *     cell and propagates through every enclosing operation. The call returns
 *     KHZ_SHEET_OK and kind is KHZ_CELL_ERROR, because the evaluation
 *     succeeded in producing the answer the model defines.
 *
 *   - An ENGINE FAILURE is a status. A null argument, a cycle, an exhausted
 *     arena or a tree deeper than KHZ_FORMULA_MAX_DEPTH are not values a cell
 *     can hold, so they come back as KHZ_SHEET_ERR_* and *out is untouched.
 *
 * Conflating the two is how spreadsheets end up showing 0 where they should
 * show #VALUE!. Callers that want the raw status instead of the error value -
 * a test, or a caller that must distinguish exact overflow from a user-visible
 * #NUM! - use khz_formula_eval_strict. */
typedef struct KhzFormulaResult {
    uint32_t    kind;    /* KhzCellKind: RATIONAL or ERROR */
    uint32_t    error;   /* KhzCellError when kind is ERROR */
    KhzRational value;   /* valid when kind is RATIONAL */
} KhzFormulaResult;

/* ------------------------------ building ------------------------------ */

/* Takes an arena mark. khz_formula_abandon returns every node to the arena;
   a formula that is kept alive must not be abandoned. */
KhzSheetStatus khz_formula_begin(KhzFormula *formula, KhzArena *arena,
                                 uint32_t col, uint32_t row);
void khz_formula_abandon(KhzFormula *formula);

KhzSheetStatus khz_formula_const(KhzFormula *formula, KhzRational value,
                                 KhzFormulaNode **out);
KhzSheetStatus khz_formula_ref(KhzFormula *formula, uint32_t col, uint32_t row,
                               KhzFormulaNode **out);
KhzSheetStatus khz_formula_range(KhzFormula *formula,
                                 uint32_t col0, uint32_t row0,
                                 uint32_t col1, uint32_t row1,
                                 KhzFormulaNode **out);

/* children is copied into the arena; the caller's array need not outlive the
   call. Arity is checked against the operation: DIV with three children is
   refused rather than silently ignoring the third. */
KhzSheetStatus khz_formula_node(KhzFormula *formula, KhzFormulaOp op,
                                KhzFormulaNode *const *children,
                                size_t child_count, KhzFormulaNode **out);

KhzSheetStatus khz_formula_set_root(KhzFormula *formula, KhzFormulaNode *root);

/* ------------------------------ parsing ------------------------------- */

/* Parses A1-style source into the tree. A leading '=' is optional.

   Numeric literals are exact: 0.1 becomes 1/10, not the nearest double. That
   is the whole point of the rational core, and it is decided here at the
   lexer, because a literal that arrives as a double has already lost.

   error may be NULL. On failure the arena is rewound to the entry mark. */
KhzSheetStatus khz_formula_parse(KhzFormula *formula, KhzArena *arena,
                                 uint32_t col, uint32_t row,
                                 const char *source, size_t len,
                                 KhzFormulaParseError *error);

/* A1 reference text to zero-based coordinates. Absolute markers are accepted
   and ignored: $A$1 and A1 name the same cell, and this engine has no copy
   operation yet for the distinction to matter to. */
KhzSheetStatus khz_formula_parse_ref(const char *text, size_t len,
                                     uint32_t *col, uint32_t *row);

/* -------------------------- evaluation -------------------------------- */

/* Evaluates against the sheet's current cell values. Reads only; no cell is
   written and the chain head does not move. */
KhzSheetStatus khz_formula_eval(struct KhzSheet *sheet, const KhzFormula *formula,
                                KhzFormulaResult *out);

/* As above, but a spreadsheet error is returned as its status code rather than
   converted into a value: #DIV/0! becomes KHZ_SHEET_ERR_DIVZERO, a text operand
   becomes KHZ_SHEET_ERR_TYPE, an unrepresentable result becomes
   KHZ_SHEET_ERR_OVERFLOW. */
KhzSheetStatus khz_formula_eval_strict(struct KhzSheet *sheet,
                                       const KhzFormula *formula,
                                       KhzRational *out);

/* Declares an edge into the dependency graph for every cell the formula reads.
   A RANGE contributes one edge per cell in the rectangle, so the topological
   order is correct rather than approximately correct. *declared receives the
   edge count and may be NULL. */
KhzSheetStatus khz_formula_declare_dependencies(struct KhzSheet *sheet,
                                                const KhzFormula *formula,
                                                size_t *declared);

/* Parse, store the source on the cell through khz_sheet_set_formula, and
   declare dependencies, as one operation. Nothing is committed if any step
   fails. */
KhzSheetStatus khz_formula_set(struct KhzSheet *sheet, uint32_t col, uint32_t row,
                               const char *source, size_t len,
                               KhzFormulaParseError *error);

/* Recalculates every formula cell in topological order, writing each result
   back with the matching khz_sheet setter so every recalculated cell is
   committed onto the proof chain.

   A circular reference is KHZ_SHEET_ERR_CYCLE and nothing is written: the
   cycle is reported, never broken at an arbitrary edge to force progress.

   *evaluated receives the number of formula cells recomputed and may be NULL. */
KhzSheetStatus khz_formula_recalc(struct KhzSheet *sheet, size_t *evaluated);

const char *khz_formula_op_name(KhzFormulaOp op);

#ifdef __cplusplus
}
#endif

#endif /* KHZ_FORMULA_H */
