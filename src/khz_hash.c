#include "khz_hash.h"

#include <string.h>

static const uint32_t khz_sha256_k[64] = {
    0x428a2f98u, 0x71374491u, 0xb5c0fbcfu, 0xe9b5dba5u,
    0x3956c25bu, 0x59f111f1u, 0x923f82a4u, 0xab1c5ed5u,
    0xd807aa98u, 0x12835b01u, 0x243185beu, 0x550c7dc3u,
    0x72be5d74u, 0x80deb1feu, 0x9bdc06a7u, 0xc19bf174u,
    0xe49b69c1u, 0xefbe4786u, 0x0fc19dc6u, 0x240ca1ccu,
    0x2de92c6fu, 0x4a7484aau, 0x5cb0a9dcu, 0x76f988dau,
    0x983e5152u, 0xa831c66du, 0xb00327c8u, 0xbf597fc7u,
    0xc6e00bf3u, 0xd5a79147u, 0x06ca6351u, 0x14292967u,
    0x27b70a85u, 0x2e1b2138u, 0x4d2c6dfcu, 0x53380d13u,
    0x650a7354u, 0x766a0abbu, 0x81c2c92eu, 0x92722c85u,
    0xa2bfe8a1u, 0xa81a664bu, 0xc24b8b70u, 0xc76c51a3u,
    0xd192e819u, 0xd6990624u, 0xf40e3585u, 0x106aa070u,
    0x19a4c116u, 0x1e376c08u, 0x2748774cu, 0x34b0bcb5u,
    0x391c0cb3u, 0x4ed8aa4au, 0x5b9cca4fu, 0x682e6ff3u,
    0x748f82eeu, 0x78a5636fu, 0x84c87814u, 0x8cc70208u,
    0x90befffau, 0xa4506cebu, 0xbef9a3f7u, 0xc67178f2u
};

static const uint32_t khz_sha256_h0[8] = {
    0x6a09e667u, 0xbb67ae85u, 0x3c6ef372u, 0xa54ff53au,
    0x510e527fu, 0x9b05688cu, 0x1f83d9abu, 0x5be0cd19u
};

static uint32_t khz_rotr32(uint32_t value, unsigned int count)
{
    return (uint32_t)((value >> count) | (value << (32u - count)));
}

static uint32_t khz_load_be32(const unsigned char *p)
{
    return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16)
         | ((uint32_t)p[2] << 8)  | (uint32_t)p[3];
}

static void khz_store_be32(unsigned char *p, uint32_t value)
{
    p[0] = (unsigned char)((value >> 24) & 0xffu);
    p[1] = (unsigned char)((value >> 16) & 0xffu);
    p[2] = (unsigned char)((value >> 8) & 0xffu);
    p[3] = (unsigned char)(value & 0xffu);
}

static void khz_store_be64(unsigned char *p, uint64_t value)
{
    p[0] = (unsigned char)((value >> 56) & 0xffu);
    p[1] = (unsigned char)((value >> 48) & 0xffu);
    p[2] = (unsigned char)((value >> 40) & 0xffu);
    p[3] = (unsigned char)((value >> 32) & 0xffu);
    p[4] = (unsigned char)((value >> 24) & 0xffu);
    p[5] = (unsigned char)((value >> 16) & 0xffu);
    p[6] = (unsigned char)((value >> 8) & 0xffu);
    p[7] = (unsigned char)(value & 0xffu);
}

static void khz_sha256_compress(uint32_t state[8], const unsigned char *block)
{
    uint32_t w[64];
    uint32_t a, b, c, d, e, f, g, h;
    unsigned int i;

    for (i = 0u; i < 16u; ++i) {
        w[i] = khz_load_be32(block + (size_t)(4u * i));
    }

    for (i = 16u; i < 64u; ++i) {
        uint32_t s0 = khz_rotr32(w[i - 15u], 7u) ^ khz_rotr32(w[i - 15u], 18u)
                    ^ (uint32_t)(w[i - 15u] >> 3);
        uint32_t s1 = khz_rotr32(w[i - 2u], 17u) ^ khz_rotr32(w[i - 2u], 19u)
                    ^ (uint32_t)(w[i - 2u] >> 10);
        w[i] = (uint32_t)(w[i - 16u] + s0 + w[i - 7u] + s1);
    }

    a = state[0];
    b = state[1];
    c = state[2];
    d = state[3];
    e = state[4];
    f = state[5];
    g = state[6];
    h = state[7];

    for (i = 0u; i < 64u; ++i) {
        uint32_t big_s1 = khz_rotr32(e, 6u) ^ khz_rotr32(e, 11u) ^ khz_rotr32(e, 25u);
        uint32_t choose = (uint32_t)((e & f) ^ (~e & g));
        uint32_t t1 = (uint32_t)(h + big_s1 + choose + khz_sha256_k[i] + w[i]);
        uint32_t big_s0 = khz_rotr32(a, 2u) ^ khz_rotr32(a, 13u) ^ khz_rotr32(a, 22u);
        uint32_t majority = (uint32_t)((a & b) ^ (a & c) ^ (b & c));
        uint32_t t2 = (uint32_t)(big_s0 + majority);

        h = g;
        g = f;
        f = e;
        e = (uint32_t)(d + t1);
        d = c;
        c = b;
        b = a;
        a = (uint32_t)(t1 + t2);
    }

    state[0] = (uint32_t)(state[0] + a);
    state[1] = (uint32_t)(state[1] + b);
    state[2] = (uint32_t)(state[2] + c);
    state[3] = (uint32_t)(state[3] + d);
    state[4] = (uint32_t)(state[4] + e);
    state[5] = (uint32_t)(state[5] + f);
    state[6] = (uint32_t)(state[6] + g);
    state[7] = (uint32_t)(state[7] + h);

    memset(w, 0, sizeof w);
}

KhzHashStatus khz_sha256_init(KhzSha256 *ctx)
{
    unsigned int i;

    if (ctx == NULL) {
        return KHZ_HASH_ERR_NULL;
    }

    for (i = 0u; i < 8u; ++i) {
        ctx->state[i] = khz_sha256_h0[i];
    }

    ctx->bytes = (uint64_t)0;
    ctx->held = (size_t)0;
    ctx->finished = 0;
    memset(ctx->block, 0, sizeof ctx->block);

    return KHZ_HASH_OK;
}

KhzHashStatus khz_sha256_update(KhzSha256 *ctx, const void *data, size_t len)
{
    const unsigned char *in;

    if (ctx == NULL) {
        return KHZ_HASH_ERR_NULL;
    }

    if (ctx->finished != 0) {
        return KHZ_HASH_ERR_STATE;
    }

    if (len == (size_t)0) {
        return KHZ_HASH_OK;
    }

    if (data == NULL) {
        return KHZ_HASH_ERR_NULL;
    }

    /* The padding block encodes the message length in bits as a 64-bit field.
       A byte count above 2^61 - 1 cannot be represented, so it is refused
       rather than silently wrapped. */
    if (len > (uint64_t)0x1fffffffffffffffull - ctx->bytes) {
        return KHZ_HASH_ERR_RANGE;
    }

    in = (const unsigned char *)data;
    ctx->bytes += (uint64_t)len;

    if (ctx->held != (size_t)0) {
        size_t want = (size_t)KHZ_SHA256_BLOCK_BYTES - ctx->held;
        size_t take = len < want ? len : want;

        memcpy(ctx->block + ctx->held, in, take);
        ctx->held += take;
        in += take;
        len -= take;

        if (ctx->held < (size_t)KHZ_SHA256_BLOCK_BYTES) {
            return KHZ_HASH_OK;
        }

        khz_sha256_compress(ctx->state, ctx->block);
        ctx->held = (size_t)0;
    }

    while (len >= (size_t)KHZ_SHA256_BLOCK_BYTES) {
        khz_sha256_compress(ctx->state, in);
        in += (size_t)KHZ_SHA256_BLOCK_BYTES;
        len -= (size_t)KHZ_SHA256_BLOCK_BYTES;
    }

    if (len != (size_t)0) {
        memcpy(ctx->block, in, len);
        ctx->held = len;
    }

    return KHZ_HASH_OK;
}

KhzHashStatus khz_sha256_final(KhzSha256 *ctx, unsigned char out[KHZ_SHA256_DIGEST_BYTES])
{
    unsigned char tail[2u * KHZ_SHA256_BLOCK_BYTES];
    size_t pad;
    size_t total;
    unsigned int i;

    if (ctx == NULL || out == NULL) {
        return KHZ_HASH_ERR_NULL;
    }

    if (ctx->finished != 0) {
        return KHZ_HASH_ERR_STATE;
    }

    memset(tail, 0, sizeof tail);
    memcpy(tail, ctx->block, ctx->held);
    tail[ctx->held] = 0x80u;

    /* One block when the 0x80 byte and the 8-byte length both fit, two when
       they do not. 56 is the boundary, and a held count of exactly 56 takes
       the two-block path. */
    pad = ctx->held < (size_t)56 ? (size_t)56 : (size_t)120;
    total = pad + (size_t)8;
    khz_store_be64(tail + pad, (uint64_t)(ctx->bytes << 3));

    khz_sha256_compress(ctx->state, tail);
    if (total > (size_t)KHZ_SHA256_BLOCK_BYTES) {
        khz_sha256_compress(ctx->state, tail + KHZ_SHA256_BLOCK_BYTES);
    }

    for (i = 0u; i < 8u; ++i) {
        khz_store_be32(out + (size_t)(4u * i), ctx->state[i]);
    }

    ctx->finished = 1;
    memset(tail, 0, sizeof tail);
    memset(ctx->block, 0, sizeof ctx->block);
    ctx->held = (size_t)0;

    return KHZ_HASH_OK;
}

KhzHashStatus khz_sha256_bytes(const void *data, size_t len, unsigned char out[KHZ_SHA256_DIGEST_BYTES])
{
    KhzSha256 ctx;
    KhzHashStatus status;

    if (out == NULL) {
        return KHZ_HASH_ERR_NULL;
    }

    status = khz_sha256_init(&ctx);
    if (status != KHZ_HASH_OK) {
        return status;
    }

    status = khz_sha256_update(&ctx, data, len);
    if (status != KHZ_HASH_OK) {
        return status;
    }

    return khz_sha256_final(&ctx, out);
}

KhzHashStatus khz_sha256_hex(const unsigned char digest[KHZ_SHA256_DIGEST_BYTES], char out[KHZ_SHA256_HEX_BYTES])
{
    static const char digits[] = "0123456789abcdef";
    unsigned int i;

    if (digest == NULL || out == NULL) {
        return KHZ_HASH_ERR_NULL;
    }

    for (i = 0u; i < KHZ_SHA256_DIGEST_BYTES; ++i) {
        out[2u * i] = digits[(digest[i] >> 4) & 0x0fu];
        out[(2u * i) + 1u] = digits[digest[i] & 0x0fu];
    }

    out[2u * KHZ_SHA256_DIGEST_BYTES] = '\0';
    return KHZ_HASH_OK;
}

uint64_t khz_sha256_message_bytes(const KhzSha256 *ctx)
{
    return ctx == NULL ? (uint64_t)0 : ctx->bytes;
}

int khz_hash_equal_ct(const void *a, const void *b, size_t len)
{
    const unsigned char *x;
    const unsigned char *y;
    unsigned char diff = 0u;
    size_t i;

    if (a == NULL || b == NULL) {
        return 0;
    }

    x = (const unsigned char *)a;
    y = (const unsigned char *)b;

    for (i = (size_t)0; i < len; ++i) {
        diff = (unsigned char)(diff | (unsigned char)(x[i] ^ y[i]));
    }

    return diff == 0u ? 1 : 0;
}

uint32_t khz_fnv1a32_seed(uint32_t seed, const void *data, size_t len)
{
    const unsigned char *in;
    size_t i;

    if (data == NULL) {
        return seed;
    }

    in = (const unsigned char *)data;

    for (i = (size_t)0; i < len; ++i) {
        seed ^= (uint32_t)in[i];
        seed = (uint32_t)(seed * KHZ_FNV1A32_PRIME);
    }

    return seed;
}

uint32_t khz_fnv1a32(const void *data, size_t len)
{
    return khz_fnv1a32_seed((uint32_t)KHZ_FNV1A32_OFFSET, data, len);
}

uint64_t khz_fnv1a64_seed(uint64_t seed, const void *data, size_t len)
{
    const unsigned char *in;
    size_t i;

    if (data == NULL) {
        return seed;
    }

    in = (const unsigned char *)data;

    for (i = (size_t)0; i < len; ++i) {
        seed ^= (uint64_t)in[i];
        seed = (uint64_t)(seed * KHZ_FNV1A64_PRIME);
    }

    return seed;
}

uint64_t khz_fnv1a64(const void *data, size_t len)
{
    return khz_fnv1a64_seed((uint64_t)KHZ_FNV1A64_OFFSET, data, len);
}

const char *khz_hash_status_name(KhzHashStatus status)
{
    switch (status) {
        case KHZ_HASH_OK:
            return "OK";
        case KHZ_HASH_ERR_NULL:
            return "ERR_NULL";
        case KHZ_HASH_ERR_STATE:
            return "ERR_STATE";
        case KHZ_HASH_ERR_RANGE:
            return "ERR_RANGE";
        default:
            return "ERR_UNKNOWN";
    }
}

int khz_sha256_selftest(void)
{
    static const struct {
        const char *message;
        const char *expect;
    } vectors[] = {
        { "",
          "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" },
        { "abc",
          "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad" },
        { "abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq",
          "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1" },
        { "abcdefghbcdefghicdefghijdefghijkefghijklfghijklmghijklmnhijklmnoijklmnopjklmnopqklmnopqrlmnopqrsmnopqrstnopqrstu",
          "cf5b16a778af8380036ce59e7b0492370b249b11e8f07a51afac45037afee9d1" }
    };

    const size_t count = sizeof vectors / sizeof vectors[0];
    size_t index;
    int failures = 0;

    for (index = (size_t)0; index < count; ++index) {
        unsigned char digest[KHZ_SHA256_DIGEST_BYTES];
        char hex[KHZ_SHA256_HEX_BYTES];
        size_t len = strlen(vectors[index].message);

        if (khz_sha256_bytes(vectors[index].message, len, digest) != KHZ_HASH_OK) {
            ++failures;
            continue;
        }

        if (khz_sha256_hex(digest, hex) != KHZ_HASH_OK) {
            ++failures;
            continue;
        }

        if (strcmp(hex, vectors[index].expect) != 0) {
            ++failures;
            continue;
        }

        /* The same message fed one byte at a time must land on the same
           digest, or the buffering path is wrong even though the one-shot
           path is right. */
        {
            KhzSha256 ctx;
            unsigned char streamed[KHZ_SHA256_DIGEST_BYTES];
            size_t offset;
            int broken = 0;

            if (khz_sha256_init(&ctx) != KHZ_HASH_OK) {
                ++failures;
                continue;
            }

            for (offset = (size_t)0; offset < len; ++offset) {
                if (khz_sha256_update(&ctx, vectors[index].message + offset, (size_t)1) != KHZ_HASH_OK) {
                    broken = 1;
                    break;
                }
            }

            if (broken != 0 || khz_sha256_final(&ctx, streamed) != KHZ_HASH_OK) {
                ++failures;
                continue;
            }

            if (khz_hash_equal_ct(digest, streamed, (size_t)KHZ_SHA256_DIGEST_BYTES) == 0) {
                ++failures;
            }
        }
    }

    return failures;
}

int khz_fnv1a_selftest(void)
{
    static const struct {
        const char *message;
        uint32_t    expect32;
        uint64_t    expect64;
    } vectors[] = {
        { "",       0x811c9dc5u, 0xcbf29ce484222325ull },
        { "a",      0xe40c292cu, 0xaf63dc4c8601ec8cull },
        { "foobar", 0xbf9cf968u, 0x85944171f73967e8ull }
    };

    const size_t count = sizeof vectors / sizeof vectors[0];
    size_t index;
    int failures = 0;

    for (index = (size_t)0; index < count; ++index) {
        size_t len = strlen(vectors[index].message);

        if (khz_fnv1a32(vectors[index].message, len) != vectors[index].expect32) {
            ++failures;
        }

        if (khz_fnv1a64(vectors[index].message, len) != vectors[index].expect64) {
            ++failures;
        }
    }

    return failures;
}
