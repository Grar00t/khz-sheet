#if !defined(_WIN32)
#  ifndef _DEFAULT_SOURCE
#    define _DEFAULT_SOURCE 1
#  endif
#  ifndef _POSIX_C_SOURCE
#    define _POSIX_C_SOURCE 200809L
#  endif
#endif

#include "khz_arena.h"

#include <stdlib.h>
#include <string.h>

#if defined(_WIN32)
#  define WIN32_LEAN_AND_MEAN 1
#  include <windows.h>
#elif defined(__unix__) || defined(__APPLE__)
#  include <sys/mman.h>
#  ifndef MAP_ANONYMOUS
#    ifdef MAP_ANON
#      define MAP_ANONYMOUS MAP_ANON
#    endif
#  endif
#endif

static int khz_is_power_of_two(size_t value)
{
    return value != (size_t)0 && (value & (value - (size_t)1)) == (size_t)0;
}

static size_t khz_align_up(size_t value, size_t alignment)
{
    return (value + (alignment - (size_t)1)) & ~(alignment - (size_t)1);
}

/* Aligns the absolute address, then converts back to an offset.

   Aligning the offset alone is only correct when the base is at least as
   aligned as the request. The malloc backing guarantees the base only to
   KHZ_ARENA_ALIGNMENT, so an offset-aligned block asked for 128 bytes of
   alignment could come back 64-byte aligned and an aligned vector store on it
   would fault. This computes what the caller actually asked for. */
static size_t khz_align_offset(uintptr_t base, size_t offset, size_t alignment)
{
    uintptr_t address = base + (uintptr_t)offset;
    uintptr_t up = (address + (uintptr_t)(alignment - (size_t)1))
                   & ~(uintptr_t)(alignment - (size_t)1);

    return (size_t)(up - base);
}

static void khz_arena_clear(KhzArena *arena)
{
    arena->base = NULL;
    arena->reservation = NULL;
    arena->reservation_bytes = (size_t)0;
    arena->capacity = (size_t)0;
    atomic_store_explicit(&arena->offset, (size_t)0, memory_order_relaxed);
    atomic_store_explicit(&arena->peak, (size_t)0, memory_order_relaxed);
    atomic_store_explicit(&arena->allocations, (uint_least64_t)0, memory_order_relaxed);
    atomic_store_explicit(&arena->rejections, (uint_least64_t)0, memory_order_relaxed);
    atomic_store_explicit(&arena->resets, (uint_least64_t)0, memory_order_relaxed);
    arena->backing = KHZ_ARENA_BACKING_NONE;
}

KhzArenaStatus khz_arena_init(KhzArena *arena, size_t bytes)
{
    size_t rounded;

    if (arena == NULL) {
        return KHZ_ARENA_ERR_NULL;
    }

    if (bytes == (size_t)0) {
        return KHZ_ARENA_ERR_RANGE;
    }

    rounded = khz_align_up(bytes, KHZ_ARENA_ALIGNMENT);
    if (rounded < bytes) {
        return KHZ_ARENA_ERR_RANGE;
    }

    khz_arena_clear(arena);

#if defined(_WIN32)
    {
        void *block = VirtualAlloc(NULL, rounded, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
        if (block != NULL) {
            arena->reservation = block;
            arena->reservation_bytes = rounded;
            arena->base = (unsigned char *)block;
            arena->capacity = rounded;
            arena->backing = KHZ_ARENA_BACKING_VIRTUAL;
            return KHZ_ARENA_OK;
        }
    }
#elif defined(MAP_ANONYMOUS)
    {
        void *block = mmap(NULL, rounded, PROT_READ | PROT_WRITE,
                           MAP_PRIVATE | MAP_ANONYMOUS, -1, (off_t)0);
        if (block != MAP_FAILED) {
            arena->reservation = block;
            arena->reservation_bytes = rounded;
            arena->base = (unsigned char *)block;
            arena->capacity = rounded;
            arena->backing = KHZ_ARENA_BACKING_MMAP;
            return KHZ_ARENA_OK;
        }
    }
#endif

    /* Last resort on a platform with neither call. One request, at startup,
       over-allocated so the usable base can be aligned by hand. This is the
       pool itself, not a per-allocation fallback: allocations still never
       touch the heap. */
    {
        size_t total = rounded + KHZ_ARENA_ALIGNMENT;
        unsigned char *raw;
        uintptr_t aligned;

        if (total < rounded) {
            return KHZ_ARENA_ERR_RANGE;
        }

        raw = (unsigned char *)malloc(total);
        if (raw == NULL) {
            return KHZ_ARENA_ERR_OS;
        }

        aligned = ((uintptr_t)raw + (uintptr_t)(KHZ_ARENA_ALIGNMENT - (size_t)1))
                  & ~(uintptr_t)(KHZ_ARENA_ALIGNMENT - (size_t)1);

        arena->reservation = raw;
        arena->reservation_bytes = total;
        arena->base = (unsigned char *)aligned;
        arena->capacity = rounded;
        arena->backing = KHZ_ARENA_BACKING_MALLOC;
        return KHZ_ARENA_OK;
    }
}

KhzArenaStatus khz_arena_init_default(KhzArena *arena)
{
    return khz_arena_init(arena, KHZ_ARENA_DEFAULT_BYTES);
}

void khz_arena_destroy(KhzArena *arena)
{
    if (arena == NULL || arena->reservation == NULL) {
        return;
    }

    switch (arena->backing) {
#if defined(_WIN32)
        case KHZ_ARENA_BACKING_VIRTUAL:
            (void)VirtualFree(arena->reservation, (SIZE_T)0, MEM_RELEASE);
            break;
#endif
#if !defined(_WIN32) && defined(MAP_ANONYMOUS)
        case KHZ_ARENA_BACKING_MMAP:
            (void)munmap(arena->reservation, arena->reservation_bytes);
            break;
#endif
        case KHZ_ARENA_BACKING_MALLOC:
            free(arena->reservation);
            break;
        default:
            break;
    }

    khz_arena_clear(arena);
}

void *khz_arena_alloc_aligned(KhzArena *arena, size_t bytes, size_t alignment)
{
    uintptr_t base;
    size_t current;

    if (arena == NULL || arena->base == NULL) {
        return NULL;
    }

    if (bytes == (size_t)0 || !khz_is_power_of_two(alignment)) {
        atomic_fetch_add_explicit(&arena->rejections, (uint_least64_t)1, memory_order_relaxed);
        return NULL;
    }

    base = (uintptr_t)arena->base;
    current = atomic_load_explicit(&arena->offset, memory_order_relaxed);

    for (;;) {
        size_t aligned = khz_align_offset(base, current, alignment);
        size_t next;

        if (aligned < current || aligned > arena->capacity) {
            atomic_fetch_add_explicit(&arena->rejections, (uint_least64_t)1, memory_order_relaxed);
            return NULL;
        }

        if (bytes > arena->capacity - aligned) {
            atomic_fetch_add_explicit(&arena->rejections, (uint_least64_t)1, memory_order_relaxed);
            return NULL;
        }

        next = aligned + bytes;

        if (atomic_compare_exchange_weak_explicit(&arena->offset, &current, next,
                                                  memory_order_acq_rel,
                                                  memory_order_relaxed)) {
            size_t seen = atomic_load_explicit(&arena->peak, memory_order_relaxed);

            while (next > seen) {
                if (atomic_compare_exchange_weak_explicit(&arena->peak, &seen, next,
                                                          memory_order_acq_rel,
                                                          memory_order_relaxed)) {
                    break;
                }
            }

            atomic_fetch_add_explicit(&arena->allocations, (uint_least64_t)1, memory_order_relaxed);
            return arena->base + aligned;
        }
    }
}

void *khz_arena_alloc(KhzArena *arena, size_t bytes)
{
    return khz_arena_alloc_aligned(arena, bytes, KHZ_ARENA_ALIGNMENT);
}

void *khz_arena_alloc_zeroed(KhzArena *arena, size_t bytes)
{
    void *block = khz_arena_alloc_aligned(arena, bytes, KHZ_ARENA_ALIGNMENT);

    if (block != NULL) {
        memset(block, 0, bytes);
    }

    return block;
}

KhzArenaStatus khz_arena_reset(KhzArena *arena)
{
    if (arena == NULL) {
        return KHZ_ARENA_ERR_NULL;
    }

    if (arena->base == NULL) {
        return KHZ_ARENA_ERR_STATE;
    }

    atomic_store_explicit(&arena->offset, (size_t)0, memory_order_release);
    atomic_fetch_add_explicit(&arena->resets, (uint_least64_t)1, memory_order_relaxed);
    return KHZ_ARENA_OK;
}

size_t khz_arena_mark(const KhzArena *arena)
{
    if (arena == NULL) {
        return (size_t)0;
    }

    return atomic_load_explicit(&arena->offset, memory_order_acquire);
}

KhzArenaStatus khz_arena_release(KhzArena *arena, size_t mark)
{
    size_t current;

    if (arena == NULL) {
        return KHZ_ARENA_ERR_NULL;
    }

    if (arena->base == NULL) {
        return KHZ_ARENA_ERR_STATE;
    }

    current = atomic_load_explicit(&arena->offset, memory_order_acquire);

    if (mark > current) {
        return KHZ_ARENA_ERR_RANGE;
    }

    atomic_store_explicit(&arena->offset, mark, memory_order_release);
    return KHZ_ARENA_OK;
}

size_t khz_arena_capacity(const KhzArena *arena)
{
    return arena == NULL ? (size_t)0 : arena->capacity;
}

size_t khz_arena_used(const KhzArena *arena)
{
    if (arena == NULL) {
        return (size_t)0;
    }

    return atomic_load_explicit(&arena->offset, memory_order_acquire);
}

size_t khz_arena_remaining(const KhzArena *arena)
{
    size_t used;

    if (arena == NULL) {
        return (size_t)0;
    }

    used = atomic_load_explicit(&arena->offset, memory_order_acquire);
    return used >= arena->capacity ? (size_t)0 : arena->capacity - used;
}

size_t khz_arena_peak(const KhzArena *arena)
{
    if (arena == NULL) {
        return (size_t)0;
    }

    return atomic_load_explicit(&arena->peak, memory_order_acquire);
}

uint64_t khz_arena_allocations(const KhzArena *arena)
{
    if (arena == NULL) {
        return (uint64_t)0;
    }

    return (uint64_t)atomic_load_explicit(&arena->allocations, memory_order_acquire);
}

uint64_t khz_arena_rejections(const KhzArena *arena)
{
    if (arena == NULL) {
        return (uint64_t)0;
    }

    return (uint64_t)atomic_load_explicit(&arena->rejections, memory_order_acquire);
}

uint64_t khz_arena_resets(const KhzArena *arena)
{
    if (arena == NULL) {
        return (uint64_t)0;
    }

    return (uint64_t)atomic_load_explicit(&arena->resets, memory_order_acquire);
}

KhzArenaBacking khz_arena_backing(const KhzArena *arena)
{
    return arena == NULL ? KHZ_ARENA_BACKING_NONE : arena->backing;
}

const char *khz_arena_status_name(KhzArenaStatus status)
{
    switch (status) {
        case KHZ_ARENA_OK:
            return "OK";
        case KHZ_ARENA_ERR_NULL:
            return "ERR_NULL";
        case KHZ_ARENA_ERR_RANGE:
            return "ERR_RANGE";
        case KHZ_ARENA_ERR_OS:
            return "ERR_OS";
        case KHZ_ARENA_ERR_STATE:
            return "ERR_STATE";
        default:
            return "ERR_UNKNOWN";
    }
}

const char *khz_arena_backing_name(KhzArenaBacking backing)
{
    switch (backing) {
        case KHZ_ARENA_BACKING_NONE:
            return "NONE";
        case KHZ_ARENA_BACKING_VIRTUAL:
            return "VIRTUAL_ALLOC";
        case KHZ_ARENA_BACKING_MMAP:
            return "MMAP";
        case KHZ_ARENA_BACKING_MALLOC:
            return "MALLOC";
        default:
            return "UNKNOWN";
    }
}
