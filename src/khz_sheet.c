#include "khz_sheet.h"

#include <string.h>

const char *khz_sheet_status_name(KhzSheetStatus status)
{
    switch (status) {
        case KHZ_SHEET_OK: return "OK";
        case KHZ_SHEET_ERR_NULL: return "ERR_NULL";
        case KHZ_SHEET_ERR_RANGE: return "ERR_RANGE";
        case KHZ_SHEET_ERR_FORMAT: return "ERR_FORMAT";
        case KHZ_SHEET_ERR_LIMIT: return "ERR_LIMIT";
        case KHZ_SHEET_ERR_MISSING: return "ERR_MISSING";
        case KHZ_SHEET_ERR_UNSUPPORTED: return "ERR_UNSUPPORTED";
        case KHZ_SHEET_ERR_OVERFLOW: return "ERR_OVERFLOW";
        case KHZ_SHEET_ERR_DIVZERO: return "ERR_DIVZERO";
        case KHZ_SHEET_ERR_STATE: return "ERR_STATE";
        case KHZ_SHEET_ERR_MEMORY: return "ERR_MEMORY";
        case KHZ_SHEET_ERR_CYCLE: return "ERR_CYCLE";
        case KHZ_SHEET_ERR_TYPE: return "ERR_TYPE";
        case KHZ_SHEET_ERR_OS: return "ERR_OS";
        default: return "ERR_UNKNOWN";
    }
}

static size_t khz_sheet_log_size(size_t cell_capacity)
{
    size_t want = cell_capacity;

    if (want < KHZ_SHEET_COMMIT_LOG_MIN) want = KHZ_SHEET_COMMIT_LOG_MIN;
    if (want > KHZ_SHEET_COMMIT_LOG_MAX) want = KHZ_SHEET_COMMIT_LOG_MAX;
    return want;
}

KhzSheetStatus khz_sheet_init(KhzSheet *sheet, size_t arena_bytes, size_t cell_capacity)
{
    KhzArenaStatus arena_status;
    KhzSheetStatus status;
    size_t log_entries;

    if (sheet == NULL) return KHZ_SHEET_ERR_NULL;
    memset(sheet, 0, sizeof *sheet);

    arena_status = khz_arena_init(&sheet->arena, arena_bytes);
    if (arena_status != KHZ_ARENA_OK) {
        return arena_status == KHZ_ARENA_ERR_RANGE ? KHZ_SHEET_ERR_RANGE
                                                   : KHZ_SHEET_ERR_MEMORY;
    }

    status = khz_grid_init(&sheet->grid, &sheet->arena, cell_capacity);
    if (status != KHZ_SHEET_OK) {
        khz_arena_destroy(&sheet->arena);
        return status;
    }

    status = khz_dep_init(&sheet->deps, &sheet->arena, &sheet->grid);
    if (status != KHZ_SHEET_OK) {
        khz_arena_destroy(&sheet->arena);
        return status;
    }

    log_entries = khz_sheet_log_size(cell_capacity);
    sheet->log.entries = (KhzCommitEntry *)khz_arena_alloc_zeroed(
        &sheet->arena, log_entries * sizeof(KhzCommitEntry));
    if (sheet->log.entries == NULL) {
        khz_arena_destroy(&sheet->arena);
        return KHZ_SHEET_ERR_MEMORY;
    }

    sheet->log.capacity = log_entries;
    sheet->log.next = (size_t)0;
    sheet->log.recorded = (uint64_t)0;
    sheet->log.dropped = (uint64_t)0;
    memset(sheet->log.base, 0, sizeof sheet->log.base);
    memset(sheet->proof, 0, sizeof sheet->proof);
    sheet->commits = (uint64_t)0;
    sheet->initialised = 1;
    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_sheet_init_default(KhzSheet *sheet)
{
    return khz_sheet_init(sheet, KHZ_ARENA_DEFAULT_BYTES, KHZ_SHEET_DEFAULT_CELLS);
}

void khz_sheet_destroy(KhzSheet *sheet)
{
    if (sheet == NULL) return;
    khz_arena_destroy(&sheet->arena);
    memset(sheet, 0, sizeof *sheet);
}

KhzArena *khz_sheet_arena(KhzSheet *sheet)
{
    return sheet == NULL ? NULL : &sheet->arena;
}

size_t khz_sheet_cell_count(const KhzSheet *sheet)
{
    return sheet == NULL ? (size_t)0 : khz_grid_count(&sheet->grid);
}

uint64_t khz_sheet_commits(const KhzSheet *sheet)
{
    return sheet == NULL ? (uint64_t)0 : sheet->commits;
}

size_t khz_sheet_log_capacity(const KhzSheet *sheet)
{
    return sheet == NULL ? (size_t)0 : sheet->log.capacity;
}

uint64_t khz_sheet_log_recorded(const KhzSheet *sheet)
{
    return sheet == NULL ? (uint64_t)0 : sheet->log.recorded;
}

uint64_t khz_sheet_log_dropped(const KhzSheet *sheet)
{
    return sheet == NULL ? (uint64_t)0 : sheet->log.dropped;
}

size_t khz_sheet_mark(const KhzSheet *sheet)
{
    return sheet == NULL ? (size_t)0 : khz_arena_mark(&sheet->arena);
}

KhzSheetStatus khz_sheet_release(KhzSheet *sheet, size_t mark)
{
    KhzArenaStatus status;

    if (sheet == NULL) return KHZ_SHEET_ERR_NULL;
    if (sheet->initialised == 0) return KHZ_SHEET_ERR_STATE;

    status = khz_arena_release(&sheet->arena, mark);
    if (status == KHZ_ARENA_ERR_RANGE) return KHZ_SHEET_ERR_RANGE;
    return status == KHZ_ARENA_OK ? KHZ_SHEET_OK : KHZ_SHEET_ERR_STATE;
}

/* Everything after a successful cell mutation must be infallible. This check
   is intentionally separate from khz_cell_commit so setters can run it before
   they modify an existing cell or create a new grid entry. */
static KhzSheetStatus khz_sheet_commit_budget(const KhzSheet *sheet)
{
    if (sheet == NULL) return KHZ_SHEET_ERR_NULL;
    if (sheet->log.entries == NULL || sheet->log.capacity == (size_t)0
        || sheet->log.next >= sheet->log.capacity) {
        return KHZ_SHEET_ERR_STATE;
    }
    if (sheet->commits == UINT64_MAX || sheet->log.recorded == UINT64_MAX) {
        return KHZ_SHEET_ERR_OVERFLOW;
    }
    return KHZ_SHEET_OK;
}

static KhzSheetStatus khz_sheet_commit_preflight(const KhzSheet *sheet,
                                                 const KhzCell *cell)
{
    KhzSheetStatus status = khz_sheet_commit_budget(sheet);
    if (status != KHZ_SHEET_OK) return status;
    if (cell == NULL) return KHZ_SHEET_ERR_NULL;
    if (cell->revision == UINT64_MAX) return KHZ_SHEET_ERR_OVERFLOW;
    return KHZ_SHEET_OK;
}

static KhzSheetStatus khz_sheet_log_append(KhzSheet *sheet, const KhzCell *cell)
{
    KhzCommitLog *log = &sheet->log;
    KhzCommitEntry *slot;

    if (log->entries == NULL || log->capacity == (size_t)0
        || log->next >= log->capacity) {
        return KHZ_SHEET_ERR_STATE;
    }
    if (log->recorded == UINT64_MAX) return KHZ_SHEET_ERR_OVERFLOW;

    slot = &log->entries[log->next];

    if (log->recorded >= (uint64_t)log->capacity) {
        memcpy(log->base, slot->head, sizeof log->base);
        if (log->dropped != UINT64_MAX) log->dropped += (uint64_t)1;
    }

    slot->key = khz_cell_key(cell->col, cell->row);
    slot->revision = cell->revision;
    memcpy(slot->head, cell->proof, KHZ_CELL_PROOF_BYTES);
    log->next = (log->next + (size_t)1) % log->capacity;
    log->recorded += (uint64_t)1;
    return KHZ_SHEET_OK;
}

static KhzSheetStatus khz_sheet_commit_cell(KhzSheet *sheet, KhzCell *cell)
{
    KhzSheetStatus status = khz_sheet_commit_preflight(sheet, cell);

    if (status != KHZ_SHEET_OK) return status;

    status = khz_cell_commit(cell, sheet->proof);
    if (status != KHZ_SHEET_OK) return status;

    /* The preflight above makes the remaining state transitions infallible:
       no counter can overflow and the log has a valid destination slot. */
    memcpy(sheet->proof, cell->proof, sizeof sheet->proof);
    sheet->commits += (uint64_t)1;
    return khz_sheet_log_append(sheet, cell);
}

static void khz_sheet_dirty_direct(KhzSheet *sheet, size_t index)
{
    const KhzDepEdge *edge;
    size_t total;

    if (khz_dep_edge_count(&sheet->deps) == (uint64_t)0) return;
    if (sheet->deps.heads == NULL || index >= sheet->deps.capacity) return;

    total = khz_grid_count(&sheet->grid);
    for (edge = sheet->deps.heads[index]; edge != NULL; edge = edge->next) {
        if (edge->to >= total) continue;
        if (sheet->grid.cells[edge->to].kind == (uint32_t)KHZ_CELL_FORMULA) {
            sheet->grid.cells[edge->to].flags |= (uint32_t)KHZ_CELL_FLAG_DIRTY;
        }
    }
}

static KhzSheetStatus khz_sheet_ready(const KhzSheet *sheet)
{
    if (sheet == NULL) return KHZ_SHEET_ERR_NULL;
    if (sheet->initialised == 0) return KHZ_SHEET_ERR_STATE;
    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_sheet_commit_in_place(KhzSheet *sheet, KhzCell *cell)
{
    KhzSheetStatus status = khz_sheet_ready(sheet);
    if (status != KHZ_SHEET_OK) return status;
    if (cell == NULL) return KHZ_SHEET_ERR_NULL;
    return khz_sheet_commit_cell(sheet, cell);
}

static KhzSheetStatus khz_sheet_copy_text(KhzSheet *sheet, const char *text, size_t len,
                                          const char **out)
{
    char *copy;

    if (len == (size_t)0) {
        *out = NULL;
        return KHZ_SHEET_OK;
    }
    if (text == NULL) return KHZ_SHEET_ERR_NULL;
    if (len > (size_t)UINT32_MAX) return KHZ_SHEET_ERR_LIMIT;

    copy = (char *)khz_arena_alloc(&sheet->arena, len);
    if (copy == NULL) return KHZ_SHEET_ERR_MEMORY;
    memcpy(copy, text, len);
    *out = copy;
    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_sheet_set_rational(KhzSheet *sheet, uint32_t col, uint32_t row,
                                     KhzRational value)
{
    KhzCell *cell;
    size_t index = (size_t)0;
    KhzSheetStatus status = khz_sheet_ready(sheet);

    if (status != KHZ_SHEET_OK) return status;
    status = khz_sheet_commit_budget(sheet);
    if (status != KHZ_SHEET_OK) return status;
    if (!khz_rational_is_valid(value)) return KHZ_SHEET_ERR_RANGE;

    status = khz_grid_upsert(&sheet->grid, col, row, &index, &cell);
    if (status != KHZ_SHEET_OK) return status;
    status = khz_sheet_commit_preflight(sheet, cell);
    if (status != KHZ_SHEET_OK) return status;

    status = khz_cell_set_rational(cell, value);
    if (status != KHZ_SHEET_OK) return status;

    status = khz_sheet_commit_cell(sheet, cell);
    if (status == KHZ_SHEET_OK) khz_sheet_dirty_direct(sheet, index);
    return status;
}

KhzSheetStatus khz_sheet_set_i64(KhzSheet *sheet, uint32_t col, uint32_t row,
                                int64_t value)
{
    KhzRational rational;
    KhzSheetStatus status = khz_rational_from_i64(value, &rational);
    if (status != KHZ_SHEET_OK) return status;
    return khz_sheet_set_rational(sheet, col, row, rational);
}

KhzSheetStatus khz_sheet_set_text(KhzSheet *sheet, uint32_t col, uint32_t row,
                                 const char *text, size_t len)
{
    KhzCell *cell;
    const char *copy = NULL;
    size_t index = (size_t)0;
    KhzSheetStatus status = khz_sheet_ready(sheet);

    if (status != KHZ_SHEET_OK) return status;
    status = khz_sheet_commit_budget(sheet);
    if (status != KHZ_SHEET_OK) return status;

    status = khz_sheet_copy_text(sheet, text, len, &copy);
    if (status != KHZ_SHEET_OK) return status;

    status = khz_grid_upsert(&sheet->grid, col, row, &index, &cell);
    if (status != KHZ_SHEET_OK) return status;
    status = khz_sheet_commit_preflight(sheet, cell);
    if (status != KHZ_SHEET_OK) return status;

    status = khz_cell_set_text(cell, copy, (uint32_t)len);
    if (status != KHZ_SHEET_OK) return status;

    status = khz_sheet_commit_cell(sheet, cell);
    if (status == KHZ_SHEET_OK) khz_sheet_dirty_direct(sheet, index);
    return status;
}

KhzSheetStatus khz_sheet_set_bool(KhzSheet *sheet, uint32_t col, uint32_t row, int value)
{
    KhzCell *cell;
    size_t index = (size_t)0;
    KhzSheetStatus status = khz_sheet_ready(sheet);

    if (status != KHZ_SHEET_OK) return status;
    status = khz_sheet_commit_budget(sheet);
    if (status != KHZ_SHEET_OK) return status;

    status = khz_grid_upsert(&sheet->grid, col, row, &index, &cell);
    if (status != KHZ_SHEET_OK) return status;
    status = khz_sheet_commit_preflight(sheet, cell);
    if (status != KHZ_SHEET_OK) return status;

    status = khz_cell_set_bool(cell, value);
    if (status != KHZ_SHEET_OK) return status;

    status = khz_sheet_commit_cell(sheet, cell);
    if (status == KHZ_SHEET_OK) khz_sheet_dirty_direct(sheet, index);
    return status;
}

KhzSheetStatus khz_sheet_set_error(KhzSheet *sheet, uint32_t col, uint32_t row,
                                  KhzCellError error)
{
    KhzCell *cell;
    size_t index = (size_t)0;
    KhzSheetStatus status = khz_sheet_ready(sheet);

    if (status != KHZ_SHEET_OK) return status;
    status = khz_sheet_commit_budget(sheet);
    if (status != KHZ_SHEET_OK) return status;
    if (error < KHZ_CELL_ERROR_NULL || error > KHZ_CELL_ERROR_NA) {
        return KHZ_SHEET_ERR_RANGE;
    }

    status = khz_grid_upsert(&sheet->grid, col, row, &index, &cell);
    if (status != KHZ_SHEET_OK) return status;
    status = khz_sheet_commit_preflight(sheet, cell);
    if (status != KHZ_SHEET_OK) return status;

    status = khz_cell_set_error(cell, error);
    if (status != KHZ_SHEET_OK) return status;

    status = khz_sheet_commit_cell(sheet, cell);
    if (status == KHZ_SHEET_OK) khz_sheet_dirty_direct(sheet, index);
    return status;
}

KhzSheetStatus khz_sheet_set_formula(KhzSheet *sheet, uint32_t col, uint32_t row,
                                    const char *formula, size_t len)
{
    KhzCell *cell;
    const char *copy = NULL;
    size_t index = (size_t)0;
    KhzSheetStatus status = khz_sheet_ready(sheet);

    if (status != KHZ_SHEET_OK) return status;
    status = khz_sheet_commit_budget(sheet);
    if (status != KHZ_SHEET_OK) return status;

    status = khz_sheet_copy_text(sheet, formula, len, &copy);
    if (status != KHZ_SHEET_OK) return status;

    status = khz_grid_upsert(&sheet->grid, col, row, &index, &cell);
    if (status != KHZ_SHEET_OK) return status;
    status = khz_sheet_commit_preflight(sheet, cell);
    if (status != KHZ_SHEET_OK) return status;

    status = khz_cell_set_formula(cell, copy, (uint32_t)len);
    if (status != KHZ_SHEET_OK) return status;
    cell->flags |= (uint32_t)KHZ_CELL_FLAG_DIRTY;

    status = khz_sheet_commit_cell(sheet, cell);
    if (status == KHZ_SHEET_OK) khz_sheet_dirty_direct(sheet, index);
    return status;
}

KhzSheetStatus khz_sheet_get(const KhzSheet *sheet, uint32_t col, uint32_t row,
                            const KhzCell **cell)
{
    KhzCell *found;
    KhzSheetStatus status;

    if (cell == NULL) return KHZ_SHEET_ERR_NULL;
    status = khz_sheet_ready(sheet);
    if (status != KHZ_SHEET_OK) return status;
    status = khz_grid_find(&sheet->grid, col, row, NULL, &found);
    if (status != KHZ_SHEET_OK) return status;
    *cell = found;
    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_sheet_get_mutable(KhzSheet *sheet, uint32_t col, uint32_t row,
                                     KhzCell **cell)
{
    KhzSheetStatus status;
    if (cell == NULL) return KHZ_SHEET_ERR_NULL;
    status = khz_sheet_ready(sheet);
    if (status != KHZ_SHEET_OK) return status;
    return khz_grid_find(&sheet->grid, col, row, NULL, cell);
}

KhzSheetStatus khz_sheet_declare_dependency(KhzSheet *sheet,
                                            uint32_t from_col, uint32_t from_row,
                                            uint32_t to_col, uint32_t to_row)
{
    KhzSheetStatus status = khz_sheet_ready(sheet);
    if (status != KHZ_SHEET_OK) return status;
    return khz_dep_add_edge(&sheet->deps, from_col, from_row, to_col, to_row);
}

KhzSheetStatus khz_sheet_evaluation_order(KhzSheet *sheet, size_t *order,
                                          size_t capacity, size_t *count)
{
    KhzSheetStatus status = khz_sheet_ready(sheet);
    if (status != KHZ_SHEET_OK) return status;
    return khz_dep_topo(&sheet->deps, order, capacity, count);
}

KhzSheetStatus khz_sheet_mark_dirty(KhzSheet *sheet, uint32_t col, uint32_t row,
                                    uint64_t *marked)
{
    size_t origin = (size_t)0;
    size_t total;
    size_t mark;
    size_t head = (size_t)0;
    size_t tail = (size_t)0;
    size_t *queue;
    unsigned char *seen;
    uint64_t count = (uint64_t)0;
    KhzSheetStatus status = khz_sheet_ready(sheet);

    if (status != KHZ_SHEET_OK) return status;
    status = khz_grid_find(&sheet->grid, col, row, &origin, NULL);
    if (status != KHZ_SHEET_OK) return status;

    total = khz_grid_count(&sheet->grid);
    if (total == (size_t)0) {
        if (marked != NULL) *marked = (uint64_t)0;
        return KHZ_SHEET_OK;
    }

    mark = khz_arena_mark(&sheet->arena);
    queue = (size_t *)khz_arena_alloc(&sheet->arena, total * sizeof(size_t));
    seen = (unsigned char *)khz_arena_alloc_zeroed(&sheet->arena, total);
    if (queue == NULL || seen == NULL) {
        (void)khz_arena_release(&sheet->arena, mark);
        return KHZ_SHEET_ERR_MEMORY;
    }

    queue[tail++] = origin;
    seen[origin] = (unsigned char)1;

    while (head < tail) {
        size_t at = queue[head++];
        KhzCell *cell = &sheet->grid.cells[at];
        const KhzDepEdge *edge;

        if (cell->kind == (uint32_t)KHZ_CELL_FORMULA) {
            cell->flags |= (uint32_t)KHZ_CELL_FLAG_DIRTY;
            ++count;
        }
        if (sheet->deps.heads == NULL || at >= sheet->deps.capacity) continue;

        for (edge = sheet->deps.heads[at]; edge != NULL; edge = edge->next) {
            if (edge->to >= total || seen[edge->to] != (unsigned char)0) continue;
            seen[edge->to] = (unsigned char)1;
            queue[tail++] = edge->to;
        }
    }

    (void)khz_arena_release(&sheet->arena, mark);
    if (marked != NULL) *marked = count;
    return KHZ_SHEET_OK;
}

size_t khz_sheet_dirty_count(const KhzSheet *sheet)
{
    size_t total;
    size_t dirty = (size_t)0;
    size_t i;

    if (sheet == NULL || sheet->initialised == 0) return (size_t)0;
    total = khz_grid_count(&sheet->grid);
    for (i = (size_t)0; i < total; ++i) {
        if ((sheet->grid.cells[i].flags & (uint32_t)KHZ_CELL_FLAG_DIRTY) != 0u) ++dirty;
    }
    return dirty;
}

static int khz_sheet_in_range(const KhzCell *cell,
                              uint32_t col0, uint32_t row0,
                              uint32_t col1, uint32_t row1)
{
    return cell->col >= col0 && cell->col <= col1
        && cell->row >= row0 && cell->row <= row1 ? 1 : 0;
}

KhzSheetStatus khz_sheet_sum(KhzSheet *sheet,
                             uint32_t col0, uint32_t row0,
                             uint32_t col1, uint32_t row1,
                             KhzRational *out, size_t *counted)
{
    size_t total_cells;
    size_t hits = (size_t)0;
    size_t i;
    int uniform = 1;
    int64_t shared_den = (int64_t)1;
    int did_simd = 0;
    KhzSheetStatus status = khz_sheet_ready(sheet);

    if (status != KHZ_SHEET_OK) return status;
    if (out == NULL) return KHZ_SHEET_ERR_NULL;

    if (col0 > col1) { uint32_t swap = col0; col0 = col1; col1 = swap; }
    if (row0 > row1) { uint32_t swap = row0; row0 = row1; row1 = swap; }
    if (col1 >= KHZ_GRID_MAX_COLUMNS || row1 >= KHZ_GRID_MAX_ROWS) {
        return KHZ_SHEET_ERR_LIMIT;
    }

    total_cells = khz_grid_count(&sheet->grid);
    for (i = (size_t)0; i < total_cells; ++i) {
        const KhzCell *cell = &sheet->grid.cells[i];
        if (khz_sheet_in_range(cell, col0, row0, col1, row1) == 0) continue;
        if (cell->kind == (uint32_t)KHZ_CELL_ERROR) return KHZ_SHEET_ERR_TYPE;
        if (cell->kind != (uint32_t)KHZ_CELL_RATIONAL) continue;
        if (hits == (size_t)0) shared_den = cell->value.den;
        else if (cell->value.den != shared_den) uniform = 0;
        ++hits;
    }

    if (hits == (size_t)0) {
        *out = khz_rational_zero();
        if (counted != NULL) *counted = (size_t)0;
        return KHZ_SHEET_OK;
    }

    if (uniform != 0) {
        size_t mark = khz_arena_mark(&sheet->arena);
        int64_t *gathered = (int64_t *)khz_arena_alloc(&sheet->arena,
                                                       hits * sizeof *gathered);
        if (gathered != NULL) {
            size_t at = (size_t)0;
            for (i = (size_t)0; i < total_cells; ++i) {
                const KhzCell *cell = &sheet->grid.cells[i];
                if (cell->kind == (uint32_t)KHZ_CELL_RATIONAL
                    && khz_sheet_in_range(cell, col0, row0, col1, row1) != 0) {
                    gathered[at++] = cell->value.num;
                }
            }
            status = khz_simd_sum_scaled(gathered, at, shared_den, out);
            (void)khz_arena_release(&sheet->arena, mark);
            if (status != KHZ_SHEET_OK) return status;
            did_simd = 1;
        } else {
            (void)khz_arena_release(&sheet->arena, mark);
        }
    }

    if (did_simd == 0) {
        KhzRational running = khz_rational_zero();
        for (i = (size_t)0; i < total_cells; ++i) {
            const KhzCell *cell = &sheet->grid.cells[i];
            if (cell->kind != (uint32_t)KHZ_CELL_RATIONAL
                || khz_sheet_in_range(cell, col0, row0, col1, row1) == 0) continue;
            status = khz_rational_add(running, cell->value, &running);
            if (status != KHZ_SHEET_OK) return status;
        }
        *out = running;
    }

    if (counted != NULL) *counted = hits;
    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_sheet_avg(KhzSheet *sheet,
                             uint32_t col0, uint32_t row0,
                             uint32_t col1, uint32_t row1,
                             KhzRational *out, size_t *counted)
{
    KhzRational total;
    KhzRational divisor;
    size_t hits = (size_t)0;
    KhzSheetStatus status;

    if (out == NULL) return KHZ_SHEET_ERR_NULL;
    status = khz_sheet_sum(sheet, col0, row0, col1, row1, &total, &hits);
    if (status != KHZ_SHEET_OK) return status;
    if (hits == (size_t)0) return KHZ_SHEET_ERR_DIVZERO;
    if (hits > (size_t)INT64_MAX) return KHZ_SHEET_ERR_OVERFLOW;

    status = khz_rational_from_i64((int64_t)hits, &divisor);
    if (status != KHZ_SHEET_OK) return status;
    status = khz_rational_div(total, divisor, out);
    if (status != KHZ_SHEET_OK) return status;
    if (counted != NULL) *counted = hits;
    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_sheet_audit_chain(const KhzSheet *sheet, KhzChainAudit *audit)
{
    unsigned char link[KHZ_SHA256_DIGEST_BYTES];
    const KhzCommitLog *log;
    size_t retained;
    size_t start;
    size_t i;
    KhzChainAudit local;
    KhzSheetStatus status = khz_sheet_ready(sheet);

    if (status != KHZ_SHEET_OK) return status;
    log = &sheet->log;
    if (log->entries == NULL) return KHZ_SHEET_ERR_STATE;

    memset(&local, 0, sizeof local);
    local.dropped = log->dropped;
    memcpy(link, log->base, sizeof link);

    retained = log->recorded < (uint64_t)log->capacity
        ? (size_t)log->recorded : log->capacity;
    start = log->recorded <= (uint64_t)log->capacity ? (size_t)0 : log->next;

    for (i = (size_t)0; i < retained; ++i) {
        const KhzCommitEntry *entry = &log->entries[(start + i) % log->capacity];
        KhzCell *cell = NULL;
        uint32_t col = 0u;
        uint32_t row = 0u;

        ++local.entries_examined;
        status = khz_cell_key_split(entry->key, &col, &row);
        if (status != KHZ_SHEET_OK) {
            local.failed_entry = i;
            if (audit != NULL) *audit = local;
            return status;
        }

        status = khz_grid_find(&sheet->grid, col, row, NULL, &cell);
        if (status != KHZ_SHEET_OK) {
            local.failed_entry = i;
            if (audit != NULL) *audit = local;
            return KHZ_SHEET_ERR_STATE;
        }

        if (cell->revision == entry->revision) {
            status = khz_cell_verify(cell, link);
            if (status != KHZ_SHEET_OK) {
                local.failed_entry = i;
                if (audit != NULL) *audit = local;
                return status;
            }
            if (khz_hash_equal_ct(cell->proof, entry->head, KHZ_CELL_PROOF_BYTES) == 0) {
                local.failed_entry = i;
                if (audit != NULL) *audit = local;
                return KHZ_SHEET_ERR_FORMAT;
            }
            ++local.cells_verified;
        } else {
            ++local.superseded;
        }
        memcpy(link, entry->head, sizeof link);
    }

    if (khz_hash_equal_ct(link, sheet->proof, sizeof link) == 0) {
        local.failed_entry = retained;
        if (audit != NULL) *audit = local;
        return KHZ_SHEET_ERR_FORMAT;
    }

    if (audit != NULL) *audit = local;
    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_sheet_verify_chain(const KhzSheet *sheet, size_t *failed_index)
{
    KhzChainAudit audit;
    KhzSheetStatus status;

    memset(&audit, 0, sizeof audit);
    if (failed_index != NULL) *failed_index = (size_t)0;

    status = khz_sheet_audit_chain(sheet, &audit);
    if (status != KHZ_SHEET_OK && failed_index != NULL) {
        *failed_index = audit.failed_entry;
    }
    return status;
}

KhzSheetStatus khz_sheet_proof_hex(const KhzSheet *sheet, char out[KHZ_SHA256_HEX_BYTES])
{
    KhzSheetStatus status = khz_sheet_ready(sheet);
    if (status != KHZ_SHEET_OK) return status;
    if (out == NULL) return KHZ_SHEET_ERR_NULL;
    return khz_sha256_hex(sheet->proof, out) == KHZ_HASH_OK ? KHZ_SHEET_OK
                                                            : KHZ_SHEET_ERR_STATE;
}
