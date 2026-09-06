#include "khz_sheet.h"

#include <string.h>

const char *khz_sheet_status_name(KhzSheetStatus status)
{
    switch (status) {
        case KHZ_SHEET_OK:
            return "OK";
        case KHZ_SHEET_ERR_NULL:
            return "ERR_NULL";
        case KHZ_SHEET_ERR_RANGE:
            return "ERR_RANGE";
        case KHZ_SHEET_ERR_FORMAT:
            return "ERR_FORMAT";
        case KHZ_SHEET_ERR_LIMIT:
            return "ERR_LIMIT";
        case KHZ_SHEET_ERR_MISSING:
            return "ERR_MISSING";
        case KHZ_SHEET_ERR_UNSUPPORTED:
            return "ERR_UNSUPPORTED";
        case KHZ_SHEET_ERR_OVERFLOW:
            return "ERR_OVERFLOW";
        case KHZ_SHEET_ERR_DIVZERO:
            return "ERR_DIVZERO";
        case KHZ_SHEET_ERR_STATE:
            return "ERR_STATE";
        case KHZ_SHEET_ERR_MEMORY:
            return "ERR_MEMORY";
        case KHZ_SHEET_ERR_CYCLE:
            return "ERR_CYCLE";
        case KHZ_SHEET_ERR_TYPE:
            return "ERR_TYPE";
        case KHZ_SHEET_ERR_OS:
            return "ERR_OS";
        default:
            return "ERR_UNKNOWN";
    }
}

/* The log is sized from the cell capacity and clamped. A sheet built with a
   1 MB arena and 1024 cells must not have a 3 MB commit log: the log is
   bookkeeping and the arena was sized for data. */
static size_t khz_sheet_log_size(size_t cell_capacity)
{
    size_t want = cell_capacity;

    if (want < KHZ_SHEET_COMMIT_LOG_MIN) {
        want = KHZ_SHEET_COMMIT_LOG_MIN;
    }
    if (want > KHZ_SHEET_COMMIT_LOG_MAX) {
        want = KHZ_SHEET_COMMIT_LOG_MAX;
    }

    return want;
}

KhzSheetStatus khz_sheet_init(KhzSheet *sheet, size_t arena_bytes, size_t cell_capacity)
{
    KhzArenaStatus arena_status;
    KhzSheetStatus status;
    size_t log_entries;

    if (sheet == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

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
        /* Without the log there is no way to verify the chain after a
           rewrite, and a sheet that cannot be verified is not a sheet this
           layer is willing to hand back. */
        khz_arena_destroy(&sheet->arena);
        return KHZ_SHEET_ERR_MEMORY;
    }

    sheet->log.capacity = log_entries;
    sheet->log.next = (size_t)0;
    sheet->log.recorded = (uint64_t)0;
    sheet->log.dropped = (uint64_t)0;
    memset(sheet->log.base, 0, sizeof sheet->log.base);

    /* Genesis link is 32 zero bytes. The first cell commit chains onto it, so
       the chain has a defined start rather than an implicit one. */
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
    if (sheet == NULL) {
        return;
    }

    /* One call returns everything: slots, cells, edge nodes, the commit log
       and copied text all came out of this arena and none of them were
       allocated separately. */
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

    if (sheet == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }
    if (sheet->initialised == 0) {
        return KHZ_SHEET_ERR_STATE;
    }

    status = khz_arena_release(&sheet->arena, mark);
    if (status == KHZ_ARENA_ERR_RANGE) {
        return KHZ_SHEET_ERR_RANGE;
    }

    return status == KHZ_ARENA_OK ? KHZ_SHEET_OK : KHZ_SHEET_ERR_STATE;
}

/* Records one commit event. Called only after the cell's proof has been
   written, so the entry stores the head as it now stands. */
static KhzSheetStatus khz_sheet_log_append(KhzSheet *sheet, const KhzCell *cell)
{
    KhzCommitLog *log = &sheet->log;
    KhzCommitEntry *slot;

    if (log->entries == NULL || log->capacity == (size_t)0) {
        return KHZ_SHEET_ERR_STATE;
    }

    slot = &log->entries[log->next];

    /* The ring is full, so this write evicts the oldest retained entry. Its
       head becomes the base the audit starts from, which keeps the retained
       window a self-contained chain rather than one with a missing first
       link. */
    if (log->recorded >= (uint64_t)log->capacity) {
        memcpy(log->base, slot->head, sizeof log->base);

        if (log->dropped != UINT64_MAX) {
            log->dropped += (uint64_t)1;
        }
    }

    slot->key = khz_cell_key(cell->col, cell->row);
    slot->revision = cell->revision;
    memcpy(slot->head, cell->proof, KHZ_CELL_PROOF_BYTES);

    log->next = (log->next + (size_t)1) % log->capacity;

    if (log->recorded == UINT64_MAX) {
        return KHZ_SHEET_ERR_OVERFLOW;
    }

    log->recorded += (uint64_t)1;
    return KHZ_SHEET_OK;
}

/* Commits the cell onto the chain head, advances the head and records the
   event. On failure the head is untouched, so a rejected write leaves no gap
   in the chain. */
static KhzSheetStatus khz_sheet_commit_cell(KhzSheet *sheet, KhzCell *cell)
{
    KhzSheetStatus status = khz_cell_commit(cell, sheet->proof);

    if (status != KHZ_SHEET_OK) {
        return status;
    }

    memcpy(sheet->proof, cell->proof, sizeof sheet->proof);

    if (sheet->commits == UINT64_MAX) {
        return KHZ_SHEET_ERR_OVERFLOW;
    }

    sheet->commits += (uint64_t)1;

    return khz_sheet_log_append(sheet, cell);
}

static KhzSheetStatus khz_sheet_ready(const KhzSheet *sheet)
{
    if (sheet == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }
    if (sheet->initialised == 0) {
        return KHZ_SHEET_ERR_STATE;
    }
    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_sheet_commit_in_place(KhzSheet *sheet, KhzCell *cell)
{
    KhzSheetStatus status = khz_sheet_ready(sheet);

    if (status != KHZ_SHEET_OK) {
        return status;
    }
    if (cell == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    return khz_sheet_commit_cell(sheet, cell);
}

/* Copies the bytes into the arena so the cell never points at caller memory
   whose lifetime this layer cannot see. */
static KhzSheetStatus khz_sheet_copy_text(KhzSheet *sheet, const char *text, size_t len,
                                          const char **out)
{
    char *copy;

    if (len == (size_t)0) {
        *out = NULL;
        return KHZ_SHEET_OK;
    }

    if (text == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    if (len > (size_t)UINT32_MAX) {
        return KHZ_SHEET_ERR_LIMIT;
    }

    copy = (char *)khz_arena_alloc(&sheet->arena, len);
    if (copy == NULL) {
        return KHZ_SHEET_ERR_MEMORY;
    }

    memcpy(copy, text, len);
    *out = copy;
    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_sheet_set_rational(KhzSheet *sheet, uint32_t col, uint32_t row,
                                     KhzRational value)
{
    KhzCell *cell;
    KhzSheetStatus status = khz_sheet_ready(sheet);

    if (status != KHZ_SHEET_OK) {
        return status;
    }

    status = khz_grid_upsert(&sheet->grid, col, row, NULL, &cell);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    status = khz_cell_set_rational(cell, value);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    return khz_sheet_commit_cell(sheet, cell);
}

KhzSheetStatus khz_sheet_set_i64(KhzSheet *sheet, uint32_t col, uint32_t row,
                                int64_t value)
{
    KhzRational rational;
    KhzSheetStatus status = khz_rational_from_i64(value, &rational);

    if (status != KHZ_SHEET_OK) {
        return status;
    }

    return khz_sheet_set_rational(sheet, col, row, rational);
}

KhzSheetStatus khz_sheet_set_text(KhzSheet *sheet, uint32_t col, uint32_t row,
                                 const char *text, size_t len)
{
    KhzCell *cell;
    const char *copy = NULL;
    KhzSheetStatus status = khz_sheet_ready(sheet);

    if (status != KHZ_SHEET_OK) {
        return status;
    }

    status = khz_sheet_copy_text(sheet, text, len, &copy);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    status = khz_grid_upsert(&sheet->grid, col, row, NULL, &cell);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    status = khz_cell_set_text(cell, copy, (uint32_t)len);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    return khz_sheet_commit_cell(sheet, cell);
}

KhzSheetStatus khz_sheet_set_bool(KhzSheet *sheet, uint32_t col, uint32_t row, int value)
{
    KhzCell *cell;
    KhzSheetStatus status = khz_sheet_ready(sheet);

    if (status != KHZ_SHEET_OK) {
        return status;
    }

    status = khz_grid_upsert(&sheet->grid, col, row, NULL, &cell);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    status = khz_cell_set_bool(cell, value);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    return khz_sheet_commit_cell(sheet, cell);
}

KhzSheetStatus khz_sheet_set_error(KhzSheet *sheet, uint32_t col, uint32_t row,
                                  KhzCellError error)
{
    KhzCell *cell;
    KhzSheetStatus status = khz_sheet_ready(sheet);

    if (status != KHZ_SHEET_OK) {
        return status;
    }

    status = khz_grid_upsert(&sheet->grid, col, row, NULL, &cell);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    status = khz_cell_set_error(cell, error);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    return khz_sheet_commit_cell(sheet, cell);
}

KhzSheetStatus khz_sheet_set_formula(KhzSheet *sheet, uint32_t col, uint32_t row,
                                    const char *formula, size_t len)
{
    KhzCell *cell;
    const char *copy = NULL;
    KhzSheetStatus status = khz_sheet_ready(sheet);

    if (status != KHZ_SHEET_OK) {
        return status;
    }

    status = khz_sheet_copy_text(sheet, formula, len, &copy);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    status = khz_grid_upsert(&sheet->grid, col, row, NULL, &cell);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    status = khz_cell_set_formula(cell, copy, (uint32_t)len);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    /* A new formula has no computed value yet, so it is dirty from birth. */
    cell->flags |= (uint32_t)KHZ_CELL_FLAG_DIRTY;

    return khz_sheet_commit_cell(sheet, cell);
}

KhzSheetStatus khz_sheet_get(const KhzSheet *sheet, uint32_t col, uint32_t row,
                            const KhzCell **cell)
{
    KhzCell *found;
    KhzSheetStatus status;

    if (cell == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    status = khz_sheet_ready(sheet);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    status = khz_grid_find(&sheet->grid, col, row, NULL, &found);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    *cell = found;
    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_sheet_get_mutable(KhzSheet *sheet, uint32_t col, uint32_t row,
                                     KhzCell **cell)
{
    KhzSheetStatus status;

    if (cell == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    status = khz_sheet_ready(sheet);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    return khz_grid_find(&sheet->grid, col, row, NULL, cell);
}

KhzSheetStatus khz_sheet_declare_dependency(KhzSheet *sheet,
                                            uint32_t from_col, uint32_t from_row,
                                            uint32_t to_col, uint32_t to_row)
{
    KhzSheetStatus status = khz_sheet_ready(sheet);

    if (status != KHZ_SHEET_OK) {
        return status;
    }

    return khz_dep_add_edge(&sheet->deps, from_col, from_row, to_col, to_row);
}

KhzSheetStatus khz_sheet_evaluation_order(KhzSheet *sheet, size_t *order,
                                          size_t capacity, size_t *count)
{
    KhzSheetStatus status = khz_sheet_ready(sheet);

    if (status != KHZ_SHEET_OK) {
        return status;
    }

    return khz_dep_topo(&sheet->deps, order, capacity, count);
}

/* Breadth-first walk of the dependents of one cell.

   An edge from A to B means B reads A, so the heads list at A is exactly the
   set of cells that must be recomputed when A changes. Only formula cells are
   marked: a value cell has nothing to recompute, and flagging it would make
   the dirty count a measure of graph reach rather than of pending work. */
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

    if (status != KHZ_SHEET_OK) {
        return status;
    }

    status = khz_grid_find(&sheet->grid, col, row, &origin, NULL);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    total = khz_grid_count(&sheet->grid);
    if (total == (size_t)0) {
        if (marked != NULL) {
            *marked = (uint64_t)0;
        }
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

        if (sheet->deps.heads == NULL || at >= sheet->deps.capacity) {
            continue;
        }

        for (edge = sheet->deps.heads[at]; edge != NULL; edge = edge->next) {
            if (edge->to >= total || seen[edge->to] != (unsigned char)0) {
                continue;
            }

            seen[edge->to] = (unsigned char)1;
            queue[tail++] = edge->to;
        }
    }

    (void)khz_arena_release(&sheet->arena, mark);

    if (marked != NULL) {
        *marked = count;
    }

    return KHZ_SHEET_OK;
}

size_t khz_sheet_dirty_count(const KhzSheet *sheet)
{
    size_t total;
    size_t dirty = (size_t)0;
    size_t i;

    if (sheet == NULL || sheet->initialised == 0) {
        return (size_t)0;
    }

    total = khz_grid_count(&sheet->grid);

    for (i = (size_t)0; i < total; ++i) {
        if ((sheet->grid.cells[i].flags & (uint32_t)KHZ_CELL_FLAG_DIRTY) != 0u) {
            ++dirty;
        }
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

    if (status != KHZ_SHEET_OK) {
        return status;
    }
    if (out == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    /* Reversed corners are normalised rather than refused: a range is a
       rectangle, and which corner was named first carries no meaning. */
    if (col0 > col1) {
        uint32_t swap = col0;
        col0 = col1;
        col1 = swap;
    }
    if (row0 > row1) {
        uint32_t swap = row0;
        row0 = row1;
        row1 = swap;
    }

    if (col1 >= KHZ_GRID_MAX_COLUMNS || row1 >= KHZ_GRID_MAX_ROWS) {
        return KHZ_SHEET_ERR_LIMIT;
    }

    /* Iteration is over the cells that exist, not over the rectangle. A range
       spanning a million empty rows costs nothing, which is what makes the
       grid virtual. */
    total_cells = khz_grid_count(&sheet->grid);

    for (i = (size_t)0; i < total_cells; ++i) {
        const KhzCell *cell = &sheet->grid.cells[i];

        if (khz_sheet_in_range(cell, col0, row0, col1, row1) == 0) {
            continue;
        }

        /* An error inside the range must propagate. This layer has nowhere to
           put it, so it refuses instead of dropping it and returning a number
           that would look like an answer. */
        if (cell->kind == (uint32_t)KHZ_CELL_ERROR) {
            return KHZ_SHEET_ERR_TYPE;
        }

        if (cell->kind != (uint32_t)KHZ_CELL_RATIONAL) {
            continue; /* blanks, text and booleans are skipped, as in Excel */
        }

        if (hits == (size_t)0) {
            shared_den = cell->value.den;
        } else if (cell->value.den != shared_den) {
            uniform = 0;
        }

        ++hits;
    }

    if (hits == (size_t)0) {
        *out = khz_rational_zero();
        if (counted != NULL) {
            *counted = (size_t)0;
        }
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

            if (status != KHZ_SHEET_OK) {
                return status;
            }

            did_simd = 1;
        } else {
            /* Scratch did not fit. The scalar fold below produces the same
               value, so the answer is unchanged - only the speed is. */
            (void)khz_arena_release(&sheet->arena, mark);
        }
    }

    if (did_simd == 0) {
        KhzRational running = khz_rational_zero();

        for (i = (size_t)0; i < total_cells; ++i) {
            const KhzCell *cell = &sheet->grid.cells[i];

            if (cell->kind != (uint32_t)KHZ_CELL_RATIONAL
                || khz_sheet_in_range(cell, col0, row0, col1, row1) == 0) {
                continue;
            }

            status = khz_rational_add(running, cell->value, &running);
            if (status != KHZ_SHEET_OK) {
                return status;
            }
        }

        *out = running;
    }

    if (counted != NULL) {
        *counted = hits;
    }

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

    if (out == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    status = khz_sheet_sum(sheet, col0, row0, col1, row1, &total, &hits);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    /* AVERAGE of an empty range is #DIV/0! in Excel, not zero. The status says
       so and the caller decides how to surface it. */
    if (hits == (size_t)0) {
        return KHZ_SHEET_ERR_DIVZERO;
    }

    if (hits > (size_t)INT64_MAX) {
        return KHZ_SHEET_ERR_OVERFLOW;
    }

    status = khz_rational_from_i64((int64_t)hits, &divisor);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    status = khz_rational_div(total, divisor, out);
    if (status != KHZ_SHEET_OK) {
        return status;
    }

    if (counted != NULL) {
        *counted = hits;
    }

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

    if (status != KHZ_SHEET_OK) {
        return status;
    }

    log = &sheet->log;

    if (log->entries == NULL) {
        return KHZ_SHEET_ERR_STATE;
    }

    memset(&local, 0, sizeof local);
    local.dropped = log->dropped;

    /* The walk starts from the head as it stood before the oldest retained
       entry. With nothing evicted that is the genesis value, 32 zero bytes. */
    memcpy(link, log->base, sizeof link);

    retained = log->recorded < (uint64_t)log->capacity
        ? (size_t)log->recorded
        : log->capacity;

    /* When the ring has wrapped, next points at the oldest retained entry. */
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
            if (audit != NULL) {
                *audit = local;
            }
            return status;
        }

        status = khz_grid_find(&sheet->grid, col, row, NULL, &cell);
        if (status != KHZ_SHEET_OK) {
            /* A commit was recorded for a coordinate that holds no cell. The
               log and the grid disagree, which is a state error, not a
               tampering finding. */
            local.failed_entry = i;
            if (audit != NULL) {
                *audit = local;
            }
            return KHZ_SHEET_ERR_STATE;
        }

        if (cell->revision == entry->revision) {
            /* This entry is the cell's current state, so the digest can be
               recomputed and must match both the link it chained onto and the
               head recorded at the time. */
            status = khz_cell_verify(cell, link);
            if (status != KHZ_SHEET_OK) {
                local.failed_entry = i;
                if (audit != NULL) {
                    *audit = local;
                }
                return status;
            }

            if (khz_hash_equal_ct(cell->proof, entry->head, KHZ_CELL_PROOF_BYTES) == 0) {
                local.failed_entry = i;
                if (audit != NULL) {
                    *audit = local;
                }
                return KHZ_SHEET_ERR_FORMAT;
            }

            ++local.cells_verified;
        } else {
            /* The cell has been written again since. Its former payload exists
               nowhere in memory, so the digest cannot be recomputed and this
               link is taken on the log's word. Counted, not hidden: an audit
               with a non-zero superseded count is a weaker statement than one
               without. */
            ++local.superseded;
        }

        memcpy(link, entry->head, sizeof link);
    }

    if (khz_hash_equal_ct(link, sheet->proof, sizeof link) == 0) {
        local.failed_entry = retained;
        if (audit != NULL) {
            *audit = local;
        }
        return KHZ_SHEET_ERR_FORMAT;
    }

    if (audit != NULL) {
        *audit = local;
    }

    return KHZ_SHEET_OK;
}

KhzSheetStatus khz_sheet_verify_chain(const KhzSheet *sheet, size_t *failed_index)
{
    KhzChainAudit audit;
    KhzSheetStatus status = khz_sheet_audit_chain(sheet, &audit);

    if (status != KHZ_SHEET_OK && failed_index != NULL) {
        *failed_index = audit.failed_entry;
    }

    return status;
}

KhzSheetStatus khz_sheet_proof_hex(const KhzSheet *sheet, char out[KHZ_SHA256_HEX_BYTES])
{
    KhzSheetStatus status = khz_sheet_ready(sheet);

    if (status != KHZ_SHEET_OK) {
        return status;
    }
    if (out == NULL) {
        return KHZ_SHEET_ERR_NULL;
    }

    return khz_sha256_hex(sheet->proof, out) == KHZ_HASH_OK ? KHZ_SHEET_OK
                                                            : KHZ_SHEET_ERR_STATE;
}

int khz_sheet_selftest(void)
{
    int failures = 0;

    failures += khz_sha256_selftest();
    failures += khz_fnv1a_selftest();

    /* Exact rational: 1/2 + 1/3 must be 5/6, not 0.8333333333333334. */
    {
        KhzRational a;
        KhzRational b;
        KhzRational sum;

        if (khz_rational_make((int64_t)1, (int64_t)2, &a) != KHZ_SHEET_OK
            || khz_rational_make((int64_t)1, (int64_t)3, &b) != KHZ_SHEET_OK
            || khz_rational_add(a, b, &sum) != KHZ_SHEET_OK
            || sum.num != (int64_t)5 || sum.den != (int64_t)6) {
            ++failures;
        }
    }

    /* Normalisation and sign placement: -2/-4 is 1/2. */
    {
        KhzRational r;

        if (khz_rational_make((int64_t)-2, (int64_t)-4, &r) != KHZ_SHEET_OK
            || r.num != (int64_t)1 || r.den != (int64_t)2) {
            ++failures;
        }
    }

    /* Division by zero is a status, never a trap and never an infinity. */
    {
        KhzRational r;

        if (khz_rational_make((int64_t)1, (int64_t)0, &r) != KHZ_SHEET_ERR_DIVZERO) {
            ++failures;
        }
    }

    /* Overflow is refused, not wrapped. */
    {
        KhzRational big;
        KhzRational one;
        KhzRational sum;

        if (khz_rational_from_i64(INT64_MAX, &big) != KHZ_SHEET_OK
            || khz_rational_from_i64((int64_t)1, &one) != KHZ_SHEET_OK
            || khz_rational_add(big, one, &sum) != KHZ_SHEET_ERR_OVERFLOW) {
            ++failures;
        }
    }

    /* The SIMD kernel must agree with the scalar fold and must refuse a sum
       that does not fit. */
    {
        int64_t values[9];
        int64_t total = (int64_t)0;
        size_t i;

        for (i = (size_t)0; i < (size_t)9; ++i) {
            values[i] = (int64_t)(i + (size_t)1);
        }

        if (khz_simd_sum_i64(values, (size_t)9, &total) != KHZ_SHEET_OK
            || total != (int64_t)45) {
            ++failures;
        }

        {
            int64_t edge[4];
            int64_t ignored = (int64_t)0;

            edge[0] = INT64_MAX;
            edge[1] = INT64_MAX;
            edge[2] = (int64_t)0;
            edge[3] = (int64_t)0;

            if (khz_simd_sum_i64(edge, (size_t)4, &ignored) != KHZ_SHEET_ERR_OVERFLOW) {
                ++failures;
            }
        }
    }

    /* MIN and MAX select, and refuse an empty input rather than inventing a
       zero for it. */
    {
        int64_t values[5];
        int64_t low = (int64_t)0;
        int64_t high = (int64_t)0;

        values[0] = (int64_t)7;
        values[1] = (int64_t)-3;
        values[2] = (int64_t)0;
        values[3] = INT64_MIN;
        values[4] = INT64_MAX;

        if (khz_simd_min_i64(values, (size_t)5, &low) != KHZ_SHEET_OK
            || low != INT64_MIN) {
            ++failures;
        }
        if (khz_simd_max_i64(values, (size_t)5, &high) != KHZ_SHEET_OK
            || high != INT64_MAX) {
            ++failures;
        }
        if (khz_simd_min_i64(values, (size_t)0, &low) != KHZ_SHEET_ERR_RANGE) {
            ++failures;
        }
    }

    /* End to end: a small sheet, an exact mean, a proof chain that verifies
       across a rewrite, and an arena that took every byte. */
    {
        KhzSheet sheet;

        if (khz_sheet_init(&sheet, (size_t)1 << 20, (size_t)1024) != KHZ_SHEET_OK) {
            ++failures;
        } else {
            KhzRational mean;
            size_t counted = (size_t)0;
            int local = 0;

            if (khz_sheet_set_i64(&sheet, 0u, 0u, (int64_t)1) != KHZ_SHEET_OK
                || khz_sheet_set_i64(&sheet, 0u, 1u, (int64_t)2) != KHZ_SHEET_OK) {
                ++local;
            }

            /* AVERAGE(1, 2) is 3/2 exactly. */
            if (khz_sheet_avg(&sheet, 0u, 0u, 0u, 1u, &mean, &counted) != KHZ_SHEET_OK
                || mean.num != (int64_t)3 || mean.den != (int64_t)2
                || counted != (size_t)2) {
                ++local;
            }

            /* Text in the range is skipped, not coerced to zero. */
            if (khz_sheet_set_text(&sheet, 0u, 2u, "label", (size_t)5) != KHZ_SHEET_OK) {
                ++local;
            }

            if (khz_sheet_avg(&sheet, 0u, 0u, 0u, 2u, &mean, &counted) != KHZ_SHEET_OK
                || mean.num != (int64_t)3 || mean.den != (int64_t)2
                || counted != (size_t)2) {
                ++local;
            }

            if (khz_sheet_verify_chain(&sheet, NULL) != KHZ_SHEET_OK) {
                ++local;
            }

            /* The Phase 94 regression: rewriting a cell that is not the most
               recently inserted one used to make the chain unverifiable,
               because the old check replayed insertion order and demanded
               revision 1. Replaying the commit log must accept it. */
            {
                KhzChainAudit audit;

                if (khz_sheet_set_i64(&sheet, 0u, 0u, (int64_t)5) != KHZ_SHEET_OK) {
                    ++local;
                }
                if (khz_sheet_audit_chain(&sheet, &audit) != KHZ_SHEET_OK) {
                    ++local;
                }
                /* One entry is now superseded: the first write to A1. */
                if (audit.superseded != (uint64_t)1) {
                    ++local;
                }
                if (audit.entries_examined != khz_sheet_log_recorded(&sheet)) {
                    ++local;
                }
                if (khz_sheet_log_dropped(&sheet) != (uint64_t)0) {
                    ++local;
                }
            }

            /* Tampering must still be caught. Editing a cell without
               committing leaves its proof stale, and the audit must say so. */
            {
                KhzCell *cell = NULL;

                if (khz_sheet_get_mutable(&sheet, 0u, 1u, &cell) != KHZ_SHEET_OK
                    || cell == NULL) {
                    ++local;
                } else {
                    KhzRational saved = cell->value;

                    cell->value.num += (int64_t)1;

                    if (khz_sheet_verify_chain(&sheet, NULL) == KHZ_SHEET_OK) {
                        ++local;
                    }

                    cell->value = saved;

                    if (khz_sheet_verify_chain(&sheet, NULL) != KHZ_SHEET_OK) {
                        ++local;
                    }
                }
            }

            /* Dirty marking follows the graph and touches formula cells only. */
            {
                uint64_t dirtied = (uint64_t)0;

                if (khz_sheet_set_formula(&sheet, 1u, 5u, "=A1+A2", (size_t)6)
                    != KHZ_SHEET_OK) {
                    ++local;
                }
                if (khz_sheet_declare_dependency(&sheet, 0u, 0u, 1u, 5u)
                    != KHZ_SHEET_OK) {
                    ++local;
                }
                if (khz_sheet_mark_dirty(&sheet, 0u, 0u, &dirtied) != KHZ_SHEET_OK
                    || dirtied != (uint64_t)1) {
                    ++local;
                }
                if (khz_sheet_dirty_count(&sheet) != (size_t)1) {
                    ++local;
                }
            }

            /* A cycle must be reported, not silently ordered. */
            if (khz_sheet_declare_dependency(&sheet, 2u, 0u, 2u, 1u) != KHZ_SHEET_OK
                || khz_sheet_declare_dependency(&sheet, 2u, 1u, 2u, 0u) != KHZ_SHEET_OK) {
                ++local;
            } else {
                size_t order[64];
                size_t produced = (size_t)0;

                if (khz_sheet_evaluation_order(&sheet, order, (size_t)64, &produced)
                    != KHZ_SHEET_ERR_CYCLE) {
                    ++local;
                }
            }

            /* Nothing may have reached the heap, and nothing may have been
               rejected in a run this small. */
            if (khz_arena_rejections(&sheet.arena) != (uint64_t)0) {
                ++local;
            }
            if (khz_arena_peak(&sheet.arena) == (size_t)0) {
                ++local;
            }

            failures += local;
            khz_sheet_destroy(&sheet);
        }
    }

    return failures;
}
