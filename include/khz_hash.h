#ifndef KHZ_HASH_H
#define KHZ_HASH_H

#include <stddef.h>
#include <stdint.h>

#if defined(__cplusplus)
extern "C" {
#endif

#define KHZ_FNV1A32_OFFSET 0x811c9dc5u
#define KHZ_FNV1A32_PRIME  0x01000193u
#define KHZ_FNV1A64_OFFSET 0xcbf29ce484222325ull
#define KHZ_FNV1A64_PRIME  0x00000100000001b3ull

#define KHZ_SHA256_DIGEST_BYTES 32u
#define KHZ_SHA256_BLOCK_BYTES  64u
#define KHZ_SHA256_HEX_BYTES    65u

typedef enum {
    KHZ_HASH_OK        = 0,
    KHZ_HASH_ERR_NULL  = -1,
    KHZ_HASH_ERR_STATE = -2,
    KHZ_HASH_ERR_RANGE = -3
} KhzHashStatus;

typedef struct {
    uint32_t      state[8];
    uint64_t      bytes;
    unsigned char block[KHZ_SHA256_BLOCK_BYTES];
    size_t        held;
    int           finished;
} KhzSha256;

/* FNV-1a is the hash-table function: fast, non-cryptographic, and used only
   where a collision costs a probe and never a wrong answer. */
uint32_t khz_fnv1a32(const void *data, size_t len);
uint32_t khz_fnv1a32_seed(uint32_t seed, const void *data, size_t len);
uint64_t khz_fnv1a64(const void *data, size_t len);
uint64_t khz_fnv1a64_seed(uint64_t seed, const void *data, size_t len);

/* SHA-256 is the proof function: it is what a cell commit is chained with, and
   it is never used for bucket selection. */
KhzHashStatus khz_sha256_init(KhzSha256 *ctx);
KhzHashStatus khz_sha256_update(KhzSha256 *ctx, const void *data, size_t len);
KhzHashStatus khz_sha256_final(KhzSha256 *ctx, unsigned char out[KHZ_SHA256_DIGEST_BYTES]);
KhzHashStatus khz_sha256_bytes(const void *data, size_t len, unsigned char out[KHZ_SHA256_DIGEST_BYTES]);
KhzHashStatus khz_sha256_hex(const unsigned char digest[KHZ_SHA256_DIGEST_BYTES], char out[KHZ_SHA256_HEX_BYTES]);

uint64_t khz_sha256_message_bytes(const KhzSha256 *ctx);
int      khz_hash_equal_ct(const void *a, const void *b, size_t len);

/* Returns the number of failed vectors. Zero means every compiled-in
   known-answer test matched. */
int khz_sha256_selftest(void);
int khz_fnv1a_selftest(void);

const char *khz_hash_status_name(KhzHashStatus status);

#if defined(__cplusplus)
}
#endif

#endif /* KHZ_HASH_H */
