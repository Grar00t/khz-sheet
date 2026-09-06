#include "khz_formula.h"

#include <string.h>

#include "khz_sheet.h"
#include "khz_simd.h"

/* ---------------------------------------------------------------- *
 * Node construction
 * ---------------------------------------------------------------- */

static KhzSheetStatus khz_node_new(KhzFormula *f, KhzFormulaOp op,
                                   KhzFormulaNode **out)
{
    KhzFormulaNode *node;

    if (f == NULL || out == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }
    if (f->arena == NULL) {
        return KHZ_SHEET_ERR_STATE;
    }
    if (f->node_count >= (uint64_t)KHZ_FORMULA_MAX_NODES) {
        return KHZ_SHEET_ERR_LIMIT;
    }

    node = (KhzFormulaNode *)khz_arena_alloc_zeroed(f->arena, sizeof *node);
    if (node == NULL) {
        return KHZ_SHEET_ERR_MEMORY;
    }

    node->op = (uint32_t)op;
    f->node_count += (uint64_t)1;
    *out = node;

    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_formula_begin(KhzFormula *formula, KhzArena *arena,
                                 uint32_t col, uint32_t row)
{
    if (formula == NULL || arena == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }
    if (col >= KHZ_GRID_MAX_COLUMNS || row >= KHZ_GRID_MAX_ROWS) {
        return KHZ_SHEET_ERR_RANGE;
    }

    memset(formula, 0, sizeof *formula);
    formula->arena = arena;
    formula->col = col;
    formula->row = row;
    formula->mark = khz_arena_mark(arena);

    return KHZ_SHEET_OK;
}

void khz_formula_abandon(KhzFormula *formula)
{
    if (formula == NULL || formula->arena == NULL) {
        return;
    }

    (void)khz_arena_release(formula->arena, formula->mark);
    memset(formula, 0, sizeof *formula);
}

KhzSheetStatus khz_formula_const(KhzFormula *formula, KhzRational value,
                                 KhzFormulaNode **out)
{
    KhzFormulaNode *node = NULL;
    KhzSheetStatus status;

    if (!khz_rational_is_valid(value)) {
        return KHZ_SHEET_ERR_RANGE;
    }

    status = khz_node_new(formula, KHZ_FORMULA_CONST, &node);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    node->value = value;
    *out = node;

    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_formula_ref(KhzFormula *formula, uint32_t col, uint32_t row,
                               KhzFormulaNode **out)
{
    KhzFormulaNode *node = NULL;
    KhzSheetStatus status;

    if (col >= KHZ_GRID_MAX_COLUMNS || row >= KHZ_GRID_MAX_ROWS) {
        return KHZ_SHEET_ERR_RANGE;
    }

    status = khz_node_new(formula, KHZ_FORMULA_REF, &node);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    node->col0 = col;
    node->row0 = row;
    node->col1 = col;
    node->row1 = row;
    formula->ref_count += (uint64_t)1;
    *out = node;

    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_formula_range(KhzFormula *formula,
                                 uint32_t col0, uint32_t row0,
                                 uint32_t col1, uint32_t row1,
                                 KhzFormulaNode **out)
{
    KhzFormulaNode *node = NULL;
    KhzSheetStatus status;

    if (col0 >= KHZ_GRID_MAX_COLUMNS || col1 >= KHZ_GRID_MAX_COLUMNS
        || row0 >= KHZ_GRID_MAX_ROWS || row1 >= KHZ_GRID_MAX_ROWS) {
        return KHZ_SHEET_ERR_RANGE;
    }

    status = khz_node_new(formula, KHZ_FORMULA_RANGE, &node);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    /* Normalised at construction: C1:A3 and A1:C3 are the same rectangle, and
       two trees for one rectangle would digest differently. */
    node->col0 = col0 < col1 ? col0 : col1;
    node->col1 = col0 < col1 ? col1 : col0;
    node->row0 = row0 < row1 ? row0 : row1;
    node->row1 = row0 < row1 ? row1 : row0;
    formula->ref_count += (uint64_t)1;
    *out = node;

    return KHZ_SHEET_OK;
}

static int khz_op_is_aggregate(KhzFormulaOp op)
{
    return op == KHZ_FORMULA_SUM || op == KHZ_FORMULA_AVG
        || op == KHZ_FORMULA_MIN || op == KHZ_FORMULA_MAX;
}

KhzSheetStatus khz_formula_node(KhzFormula *formula, KhzFormulaOp op,
                                KhzFormulaNode *const *children,
                                size_t child_count, KhzFormulaNode **out)
{
    KhzFormulaNode *node = NULL;
    KhzFormulaNode **slots;
    KhzSheetStatus status;
    size_t i;

    if (formula == NULL || out == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }
    if (child_count > (size_t)0 && children == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    /* Arity is enforced here rather than at evaluation. A DIV with three
       children would otherwise evaluate as if the third did not exist, which
       is a wrong answer instead of a refusal. */
    switch (op) {
    case KHZ_FORMULA_ADD:
    case KHZ_FORMULA_SUB:
    case KHZ_FORMULA_MUL:
    case KHZ_FORMULA_DIV:
        if (child_count != (size_t)2) {
            return KHZ_SHEET_ERR_FORMAT;
        }
        break;
    case KHZ_FORMULA_NEG:
        if (child_count != (size_t)1) {
            return KHZ_SHEET_ERR_FORMAT;
        }
        break;
    case KHZ_FORMULA_SUM:
    case KHZ_FORMULA_AVG:
    case KHZ_FORMULA_MIN:
    case KHZ_FORMULA_MAX:
        if (child_count == (size_t)0 || child_count > KHZ_FORMULA_MAX_ARGS) {
            return KHZ_SHEET_ERR_FORMAT;
        }
        break;
    default:
        /* Leaves have dedicated constructors that fill their payload. */
        return KHZ_SHEET_ERR_UNSUPPORTED;
    }

    for (i = (size_t)0; i < child_count; ++i) {
        if (children[i] == NULL) {
            return KHZ_SHEET_ERR_NULL;
        }
    }

    status = khz_node_new(formula, op, &node);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    slots = (KhzFormulaNode **)khz_arena_alloc(formula->arena,
                                               child_count * sizeof(KhzFormulaNode *));
    if (slots == NULL) {
        return KHZ_SHEET_ERR_MEMORY;
    }

    for (i = (size_t)0; i < child_count; ++i) {
        slots[i] = children[i];
    }

    node->children = slots;
    node->child_count = (uint32_t)child_count;
    *out = node;

    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_formula_set_root(KhzFormula *formula, KhzFormulaNode *root)
{
    if (formula == NULL || root == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    formula->root = root;
    return KHZ_SHEET_OK;
}

const char *khz_formula_op_name(KhzFormulaOp op)
{
    switch (op) {
    case KHZ_FORMULA_CONST: return "CONST";
    case KHZ_FORMULA_REF:   return "REF";
    case KHZ_FORMULA_RANGE: return "RANGE";
    case KHZ_FORMULA_ADD:   return "ADD";
    case KHZ_FORMULA_SUB:   return "SUB";
    case KHZ_FORMULA_MUL:   return "MUL";
    case KHZ_FORMULA_DIV:   return "DIV";
    case KHZ_FORMULA_NEG:   return "NEG";
    case KHZ_FORMULA_SUM:   return "SUM";
    case KHZ_FORMULA_AVG:   return "AVG";
    case KHZ_FORMULA_MIN:   return "MIN";
    case KHZ_FORMULA_MAX:   return "MAX";
    default:                return "UNKNOWN";
    }
}

/* ---------------------------------------------------------------- *
 * Reference text
 * ---------------------------------------------------------------- */

static int khz_is_alpha(char c)
{
    return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
}

static int khz_is_digit(char c)
{
    return c >= '0' && c <= '9';
}

static char khz_upper(char c)
{
    return (c >= 'a' && c <= 'z') ? (char)(c - 'a' + 'A') : c;
}

KhzSheetStatus khz_formula_parse_ref(const char *text, size_t len,
                                     uint32_t *col, uint32_t *row)
{
    uint64_t column = (uint64_t)0;
    uint64_t line = (uint64_t)0;
    size_t i = (size_t)0;
    size_t letters = (size_t)0;
    size_t digits = (size_t)0;

    if (text == NULL || col == NULL || row == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }
    if (len == (size_t)0) {
        return KHZ_SHEET_ERR_FORMAT;
    }

    /* $ is accepted and discarded. Absolute and relative name the same cell,
       and there is no copy operation yet for the difference to affect. */
    if (i < len && text[i] == '$') {
        ++i;
    }

    while (i < len && khz_is_alpha(text[i])) {
        column = column * (uint64_t)26 + (uint64_t)(khz_upper(text[i]) - 'A' + 1);
        if (column > (uint64_t)KHZ_GRID_MAX_COLUMNS) {
            return KHZ_SHEET_ERR_RANGE;
        }
        ++letters;
        ++i;
    }

    if (i < len && text[i] == '$') {
        ++i;
    }

    while (i < len && khz_is_digit(text[i])) {
        line = line * (uint64_t)10 + (uint64_t)(text[i] - '0');
        if (line > (uint64_t)KHZ_GRID_MAX_ROWS) {
            return KHZ_SHEET_ERR_RANGE;
        }
        ++digits;
        ++i;
    }

    if (letters == (size_t)0 || digits == (size_t)0 || i != len) {
        return KHZ_SHEET_ERR_FORMAT;
    }
    if (column == (uint64_t)0 || line == (uint64_t)0) {
        return KHZ_SHEET_ERR_FORMAT;
    }

    /* A1 is the user's origin; the grid's is 0,0. */
    *col = (uint32_t)(column - (uint64_t)1);
    *row = (uint32_t)(line - (uint64_t)1);

    return KHZ_SHEET_OK;
}

/* ---------------------------------------------------------------- *
 * Recursive-descent parser
 * ---------------------------------------------------------------- */

typedef struct KhzParser {
    const char           *src;
    size_t                len;
    size_t                pos;
    size_t                depth;
    KhzFormula           *formula;
    KhzFormulaParseError *error;
} KhzParser;

static KhzSheetStatus khz_parse_expr(KhzParser *p, KhzFormulaNode **out);

static void khz_parse_fail(KhzParser *p, const char *expected)
{
    size_t n;

    if (p->error == NULL) {
        return;
    }

    p->error->offset = p->pos;

    n = strlen(expected);
    if (n >= sizeof p->error->expected) {
        n = sizeof p->error->expected - (size_t)1;
    }

    memcpy(p->error->expected, expected, n);
    p->error->expected[n] = '\0';
}

static void khz_skip_space(KhzParser *p)
{
    while (p->pos < p->len) {
        char c = p->src[p->pos];

        if (c == ' ' || c == '\t' || c == '\r' || c == '\n') {
            ++p->pos;
        } else {
            break;
        }
    }
}

static char khz_peek(KhzParser *p)
{
    return p->pos < p->len ? p->src[p->pos] : '\0';
}

/* Multiply-accumulate with an explicit ceiling. A literal that does not fit is
   refused at the lexer; wrapping it would put a number in the sheet that the
   user never typed. */
static int khz_mul10_add(int64_t *acc, int digit)
{
    if (*acc > (INT64_MAX - (int64_t)digit) / (int64_t)10) {
        return 1;
    }

    *acc = *acc * (int64_t)10 + (int64_t)digit;
    return 0;
}

static KhzSheetStatus khz_parse_number(KhzParser *p, KhzFormulaNode **out)
{
    int64_t num = (int64_t)0;
    int64_t den = (int64_t)1;
    size_t digits = (size_t)0;
    KhzRational value;
    KhzSheetStatus status;

    while (p->pos < p->len && khz_is_digit(p->src[p->pos])) {
        if (khz_mul10_add(&num, p->src[p->pos] - '0')) {
            khz_parse_fail(p, "number in range");
            return KHZ_SHEET_ERR_OVERFLOW;
        }
        ++digits;
        ++p->pos;
    }

    if (p->pos < p->len && p->src[p->pos] == '.') {
        ++p->pos;

        while (p->pos < p->len && khz_is_digit(p->src[p->pos])) {
            if (khz_mul10_add(&num, p->src[p->pos] - '0')) {
                khz_parse_fail(p, "number in range");
                return KHZ_SHEET_ERR_OVERFLOW;
            }
            if (den > INT64_MAX / (int64_t)10) {
                khz_parse_fail(p, "fewer decimal places");
                return KHZ_SHEET_ERR_OVERFLOW;
            }
            den *= (int64_t)10;
            ++digits;
            ++p->pos;
        }
    }

    if (digits == (size_t)0) {
        khz_parse_fail(p, "digit");
        return KHZ_SHEET_ERR_FORMAT;
    }

    /* 0.1 becomes 1/10 exactly. This is the single most important line in the
       parser: a literal that arrives as a double has already lost the
       precision the rest of the engine exists to preserve. */
    status = khz_rational_make(num, den, &value);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    return khz_formula_const(p->formula, value, out);
}

static KhzSheetStatus khz_parse_word(KhzParser *p, size_t *start, size_t *length)
{
    *start = p->pos;

    while (p->pos < p->len) {
        char c = p->src[p->pos];

        if (khz_is_alpha(c) || khz_is_digit(c) || c == '$') {
            ++p->pos;
        } else {
            break;
        }
    }

    *length = p->pos - *start;

    if (*length == (size_t)0) {
        khz_parse_fail(p, "identifier");
        return KHZ_SHEET_ERR_FORMAT;
    }

    return KHZ_SHEET_OK;
}

static int khz_word_matches(const char *text, size_t len, const char *name)
{
    size_t i;

    for (i = (size_t)0; i < len; ++i) {
        if (name[i] == '\0' || khz_upper(text[i]) != name[i]) {
            return 0;
        }
    }

    return name[len] == '\0';
}

static KhzSheetStatus khz_function_op(const char *text, size_t len, KhzFormulaOp *op)
{
    if (khz_word_matches(text, len, "SUM")) {
        *op = KHZ_FORMULA_SUM;
    } else if (khz_word_matches(text, len, "AVG")
               || khz_word_matches(text, len, "AVERAGE")) {
        *op = KHZ_FORMULA_AVG;
    } else if (khz_word_matches(text, len, "MIN")) {
        *op = KHZ_FORMULA_MIN;
    } else if (khz_word_matches(text, len, "MAX")) {
        *op = KHZ_FORMULA_MAX;
    } else {
        /* An unknown name is #NAME? in a spreadsheet, but at parse time there
           is no cell to put it in, so it is a format error here and the caller
           decides. */
        return KHZ_SHEET_ERR_MISSING;
    }

    return KHZ_SHEET_OK;
}

static KhzSheetStatus khz_parse_primary(KhzParser *p, KhzFormulaNode **out)
{
    KhzSheetStatus status;
    char c;

    khz_skip_space(p);
    c = khz_peek(p);

    if (c == '\0') {
        khz_parse_fail(p, "operand");
        return KHZ_SHEET_ERR_FORMAT;
    }

    if (c == '(') {
        ++p->pos;

        status = khz_parse_expr(p, out);
        if (status != KHZ_SHEET_OK) {
            return status;
        }

        khz_skip_space(p);
        if (khz_peek(p) != ')') {
            khz_parse_fail(p, ")");
            return KHZ_SHEET_ERR_FORMAT;
        }
        ++p->pos;

        return KHZ_SHEET_OK;
    }

    if (khz_is_digit(c) || c == '.') {
        return khz_parse_number(p, out);
    }

    if (khz_is_alpha(c) || c == '$') {
        size_t start = (size_t)0;
        size_t length = (size_t)0;
        KhzFormulaOp op;
        uint32_t col0 = 0u;
        uint32_t row0 = 0u;

        status = khz_parse_word(p, &start, &length);
        if (status != KHZ_SHEET_OK) {
            return status;
        }

        khz_skip_space(p);

        if (khz_peek(p) == '(') {
            KhzFormulaNode *args[KHZ_FORMULA_MAX_ARGS];
            size_t arg_count = (size_t)0;

            status = khz_function_op(p->src + start, length, &op);
            if (status != KHZ_SHEET_OK) {
                khz_parse_fail(p, "SUM AVG MIN or MAX");
                return status;
            }

            ++p->pos;

            for (;;) {
                KhzFormulaNode *arg = NULL;

                if (arg_count >= KHZ_FORMULA_MAX_ARGS) {
                    khz_parse_fail(p, "fewer arguments");
                    return KHZ_SHEET_ERR_LIMIT;
                }

                status = khz_parse_expr(p, &arg);
                if (status != KHZ_SHEET_OK) {
                    return status;
                }

                args[arg_count] = arg;
                ++arg_count;

                khz_skip_space(p);

                if (khz_peek(p) == ',') {
                    ++p->pos;
                    continue;
                }

                break;
            }

            if (khz_peek(p) != ')') {
                khz_parse_fail(p, ")");
                return KHZ_SHEET_ERR_FORMAT;
            }
            ++p->pos;

            return khz_formula_node(p->formula, op, args, arg_count, out);
        }

        status = khz_formula_parse_ref(p->src + start, length, &col0, &row0);
        if (status != KHZ_SHEET_OK) {
            khz_parse_fail(p, "cell reference");
            return status;
        }

        if (khz_peek(p) == ':') {
            size_t rstart = (size_t)0;
            size_t rlength = (size_t)0;
            uint32_t col1 = 0u;
            uint32_t row1 = 0u;

            ++p->pos;
            khz_skip_space(p);

            status = khz_parse_word(p, &rstart, &rlength);
            if (status != KHZ_SHEET_OK) {
                return status;
            }

            status = khz_formula_parse_ref(p->src + rstart, rlength, &col1, &row1);
            if (status != KHZ_SHEET_OK) {
                khz_parse_fail(p, "cell reference");
                return status;
            }

            return khz_formula_range(p->formula, col0, row0, col1, row1, out);
        }

        return khz_formula_ref(p->formula, col0, row0, out);
    }

    khz_parse_fail(p, "operand");
    return KHZ_SHEET_ERR_FORMAT;
}

static KhzSheetStatus khz_parse_unary(KhzParser *p, KhzFormulaNode **out)
{
    KhzSheetStatus status;

    khz_skip_space(p);

    if (khz_peek(p) == '-') {
        KhzFormulaNode *child = NULL;

        ++p->pos;

        if (++p->depth > KHZ_FORMULA_MAX_DEPTH) {
            khz_parse_fail(p, "shallower expression");
            return KHZ_SHEET_ERR_LIMIT;
        }

        status = khz_parse_unary(p, &child);
        --p->depth;

        if (status != KHZ_SHEET_OK) {
            return status;
        }

        return khz_formula_node(p->formula, KHZ_FORMULA_NEG, &child, (size_t)1, out);
    }

    if (khz_peek(p) == '+') {
        ++p->pos;
        return khz_parse_unary(p, out);
    }

    return khz_parse_primary(p, out);
}

static KhzSheetStatus khz_parse_term(KhzParser *p, KhzFormulaNode **out)
{
    KhzFormulaNode *left = NULL;
    KhzSheetStatus status = khz_parse_unary(p, &left);

    if (status != KHZ_SHEET_OK) {
        return status;
    }

    for (;;) {
        KhzFormulaNode *pair[2];
        KhzFormulaNode *right = NULL;
        KhzFormulaOp op;
        char c;

        khz_skip_space(p);
        c = khz_peek(p);

        if (c == '*') {
            op = KHZ_FORMULA_MUL;
        } else if (c == '/') {
            op = KHZ_FORMULA_DIV;
        } else {
            break;
        }

        ++p->pos;

        status = khz_parse_unary(p, &right);
        if (status != KHZ_SHEET_OK) {
            return status;
        }

        pair[0] = left;
        pair[1] = right;

        status = khz_formula_node(p->formula, op, pair, (size_t)2, &left);
        if (status != KHZ_SHEET_OK) {
            return status;
        }
    }

    *out = left;
    return KHZ_SHEET_OK;
}

static KhzSheetStatus khz_parse_expr(KhzParser *p, KhzFormulaNode **out)
{
    KhzFormulaNode *left = NULL;
    KhzSheetStatus status;

    if (++p->depth > KHZ_FORMULA_MAX_DEPTH) {
        khz_parse_fail(p, "shallower expression");
        return KHZ_SHEET_ERR_LIMIT;
    }

    status = khz_parse_term(p, &left);
    if (status != KHZ_SHEET_OK) {
        --p->depth;
        return status;
    }

    for (;;) {
        KhzFormulaNode *pair[2];
        KhzFormulaNode *right = NULL;
        KhzFormulaOp op;
        char c;

        khz_skip_space(p);
        c = khz_peek(p);

        if (c == '+') {
            op = KHZ_FORMULA_ADD;
        } else if (c == '-') {
            op = KHZ_FORMULA_SUB;
        } else {
            break;
        }

        ++p->pos;

        status = khz_parse_term(p, &right);
        if (status != KHZ_SHEET_OK) {
            --p->depth;
            return status;
        }

        pair[0] = left;
        pair[1] = right;

        status = khz_formula_node(p->formula, op, pair, (size_t)2, &left);
        if (status != KHZ_SHEET_OK) {
            --p->depth;
            return status;
        }
    }

    --p->depth;
    *out = left;

    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_formula_parse(KhzFormula *formula, KhzArena *arena,
                                 uint32_t col, uint32_t row,
                                 const char *source, size_t len,
                                 KhzFormulaParseError *error)
{
    KhzParser parser;
    KhzFormulaNode *root = NULL;
    KhzSheetStatus status;

    if (formula == NULL || arena == NULL || source == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }
    if (len == (size_t)0 || len > KHZ_FORMULA_MAX_SOURCE) {
        return KHZ_SHEET_ERR_RANGE;
    }

    status = khz_formula_begin(formula, arena, col, row);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    if (error != NULL) {
        error->offset = (size_t)0;
        error->expected[0] = '\0';
    }

    memset(&parser, 0, sizeof parser);
    parser.src = source;
    parser.len = len;
    parser.formula = formula;
    parser.error = error;

    /* The leading '=' is how a user marks a formula, not part of the
       expression. */
    khz_skip_space(&parser);
    if (khz_peek(&parser) == '=') {
        ++parser.pos;
    }

    status = khz_parse_expr(&parser, &root);
    if (status != KHZ_SHEET_OK) {
        khz_formula_abandon(formula);
        return status;
    }

    khz_skip_space(&parser);

    /* Trailing junk is a failure. Accepting "A1 B2" by ignoring the tail would
       evaluate half of what was written. */
    if (parser.pos != parser.len) {
        khz_parse_fail(&parser, "end of formula");
        khz_formula_abandon(formula);
        return KHZ_SHEET_ERR_FORMAT;
    }

    formula->root = root;
    return KHZ_SHEET_OK;
}
