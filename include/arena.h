#ifndef KHZ_ARENA_H
#define KHZ_ARENA_H

#include <stdatomic.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Alignment of every allocation. 64 bytes is the AVX-512 vector width and the
   common cache line size, so a block handed out here can be loaded with
   aligned SIMD instructions and never straddles two lines. */
#define KHZ_ARENA_ALIGNMENT ((size_t)64)

#define KHZ_ARENA_DEFAULT_BYTES ((size_t)64 * (size_t)1024 * (size_t)1024)

typedef enum KhzArenaStatus {
    KHZ_ARENA_OK = 0,
    KHZ_ARENA_ERR_NULL = -1,
    KHZ_ARENA_ERR_RANGE = -2,
    KHZ_ARENA_ERR_OS = -3,
    KHZ_ARENA_ERR_STATE = -4
} KhzArenaStatus;

typedef enum KhzArenaBacking {
    KHZ_ARENA_BACKING_NONE = 0,
    KHZ_ARENA_BACKING_VIRTUAL = 1,
    KHZ_ARENA_BACKING_MMAP = 2,
    KHZ_ARENA_BACKING_MALLOC = 3
} KhzArenaBacking;

/* One reservation, one bump pointer. Nothing is released individually; the
   whole region is reset or destroyed at once, so fragmentation is not a state
   this allocator can enter. */
typedef struct KhzArena {
    unsigned char *base;
    void *reservation;
    size_t reservation_bytes;
    size_t capacity;
    atomic_size_t offset;
    atomic_size_t peak;
    atomic_uint_least64_t allocations;
    atomic_uint_least64_t rejections;
    atomic_uint_least64_t resets;
    KhzArenaBacking backing;
} KhzArena;

KhzArenaStatus khz_arena_init(KhzArena *arena, size_t bytes);
KhzArenaStatus khz_arena_init_default(KhzArena *arena);
void khz_arena_destroy(KhzArena *arena);

/* Returns NULL on exhaustion and counts the rejection. It does not fall back
   to the heap: a caller that silently grows past its reservation has lost the
   property the arena exists to provide. */
void *khz_arena_alloc(KhzArena *arena, size_t bytes);
void *khz_arena_alloc_aligned(KhzArena *arena, size_t bytes, size_t alignment);
void *khz_arena_alloc_zeroed(KhzArena *arena, size_t bytes);

/* Reset and release are not synchronised against concurrent allocation. They
   are safe only when the caller knows no other thread is allocating; that is a
   condition on the caller, not a promise this code can keep for it. */
KhzArenaStatus khz_arena_reset(KhzArena *arena);
size_t khz_arena_mark(const KhzArena *arena);
KhzArenaStatus khz_arena_release(KhzArena *arena, size_t mark);

size_t khz_arena_capacity(const KhzArena *arena);
size_t khz_arena_used(const KhzArena *arena);
size_t khz_arena_remaining(const KhzArena *arena);
size_t khz_arena_peak(const KhzArena *arena);
uint64_t khz_arena_allocations(const KhzArena *arena);
uint64_t khz_arena_rejections(const KhzArena *arena);
uint64_t khz_arena_resets(const KhzArena *arena);
KhzArenaBacking khz_arena_backing(const KhzArena *arena);

const char *khz_arena_status_name(KhzArenaStatus status);
const char *khz_arena_backing_name(KhzArenaBacking backing);

#ifdef __cplusplus
}
#endif

#endif /* KHZ_ARENA_H */
