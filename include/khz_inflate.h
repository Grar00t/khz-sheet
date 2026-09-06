/* khz_inflate.h - raw DEFLATE decompression, decode only.
 *
 * Authorship, because this replaces a request to vendor miniz.
 *
 * This is original code written for khz-sheet. It is not miniz, not puff, not
 * zlib, and carries no third-party copyright. Vendoring miniz was the stated
 * plan for Phase 97 and was refused for one reason: committing a
 * reimplementation under the miniz name, with miniz's MIT notice and its
 * authors' names attached, would be a false provenance claim in a file whose
 * whole purpose is to carry a legal notice. Writing an original decoder and
 * saying so is the only honest option available.
 *
 * The practical case is also better. This decodes and never encodes, which is
 * all a reader needs; it allocates only from KhzArena, where miniz owns its
 * own allocator and would have been the first heap in the project; and it is
 * about six hundred lines against miniz's seven thousand, all of which would
 * have to be reviewed before being trusted.
 *
 * What it implements: RFC 1951 raw DEFLATE. Stored blocks, fixed Huffman
 * blocks, dynamic Huffman blocks. That is the entire format, and it is what
 * zip method 8 holds.
 *
 * What it does not implement: the zlib wrapper of RFC 1950 and the gzip
 * wrapper of RFC 1952. Zip stores raw DEFLATE with no wrapper, so neither is
 * needed here. Passing a zlib stream to this decoder will fail on the header
 * byte rather than silently misread it.
 *
 * The output size must be known in advance. Every zip entry declares its
 * uncompressed size in both the local and the central header, so the caller
 * always knows it, and requiring it removes the growing-buffer logic that
 * would otherwise be the most delicate part of the file. A stream that tries
 * to produce more than the declared size is rejected as corrupt rather than
 * truncated quietly - a zip whose declared size disagrees with its payload is
 * not a zip worth guessing about.
 *
 * NOT COMPILED. Like every C file in this repository, this has never been
 * built or run. An inflate decoder is exactly the kind of code where that
 * matters most: it is a bit-level state machine driven by attacker-controlled
 * input, and reading it carefully is not the same as testing it. Treat every
 * claim here as a design statement until a build says otherwise.
 */
#ifndef KHZ_INFLATE_H
#define KHZ_INFLATE_H

#include <stddef.h>
#include <stdint.h>

#include "khz_arena.h"
#include "khz_status.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Largest back-reference distance DEFLATE can encode. Present for the
 * bounds check, not to size a window: this decoder writes into the caller's
 * full output buffer and copies matches out of what it has already written,
 * so there is no separate 32 KiB window to maintain. */
#define KHZ_INFLATE_WINDOW_BYTES ((size_t)32768)

/* Literal/length alphabet: 286 used, 288 allocated by the format. */
#define KHZ_INFLATE_MAX_SYMBOLS ((size_t)288)

/* Distance alphabet: 30 used, 32 encodable at five bits each. */
#define KHZ_INFLATE_MAX_DISTANCES ((size_t)32)

/* Code-length alphabet used to describe a dynamic block's two other codes. */
#define KHZ_INFLATE_MAX_LENGTH_CODES ((size_t)19)

/* Maximum Huffman code length RFC 1951 permits. */
#define KHZ_INFLATE_MAX_BITS 15

/* A canonical Huffman decoding table.
 *
 * Held as a count of codes per bit length plus symbols sorted by code, which
 * is enough to decode one bit at a time without materialising a lookup table.
 * Slower per symbol than a table-driven decoder and a great deal easier to
 * read, which is the correct trade for code that has never been tested: an
 * xlsx worksheet is measured in megabytes at worst, and the bottleneck is the
 * XML parse rather than the inflate. */
typedef struct KhzInflateHuffman {
	uint16_t counts[KHZ_INFLATE_MAX_BITS + 1];
	uint16_t symbols[KHZ_INFLATE_MAX_SYMBOLS];
	size_t symbol_count;
} KhzInflateHuffman;

/* Decoder state.
 *
 * Large enough that it should not sit on a deep stack; khz_inflate_to_arena
 * allocates it from the arena. Exposed rather than opaque so a caller can
 * place it wherever it likes, and so the counters below can be read after a
 * failure to say something about where the stream went wrong. */
typedef struct KhzInflate {
	/* Input, consumed least-significant-bit first as the format requires. */
	const unsigned char *in;
	size_t in_bytes;
	size_t in_pos;

	/* Bit accumulator. Never holds more than 32 bits. */
	uint32_t bit_buffer;
	unsigned bit_count;

	/* Output. Fixed capacity, never grown, never reallocated. */
	unsigned char *out;
	size_t out_capacity;
	size_t out_len;

	/* Diagnostics, valid after success or failure alike. */
	uint64_t blocks_stored;
	uint64_t blocks_fixed;
	uint64_t blocks_dynamic;
	int finished;

	/* Rebuilt per dynamic block; set once for fixed blocks. */
	KhzInflateHuffman lit;
	KhzInflateHuffman dist;
} KhzInflate;

/* Decompresses a complete raw DEFLATE stream into a caller-owned buffer.
 *
 * output_capacity must be the exact uncompressed size, or larger. produced
 * receives the byte count actually written and may be NULL.
 *
 * Returns KHZ_SHEET_OK on a stream that ended with its final block and
 * produced no more than the capacity allowed.
 *   ERR_NULL      input or output pointer missing
 *   ERR_RANGE     input ran out mid-symbol: the stream is truncated
 *   ERR_FORMAT    invalid block type, bad stored-block complement,
 *                 over-subscribed Huffman code, symbol outside the alphabet,
 *                 or a distance reaching back before the start of the output
 *   ERR_MEMORY    the stream tried to produce more than output_capacity
 *
 * Never allocates. Never reads outside the input range. Never writes outside
 * the output range - and the ERR_MEMORY case is a refusal to write, checked
 * before the copy rather than after. */
KhzSheetStatus khz_inflate_raw(const void *input,
                               size_t input_bytes,
                               void *output,
                               size_t output_capacity,
                               size_t *produced);

/* As khz_inflate_raw, but takes the output buffer from the arena.
 *
 * expected_bytes is the uncompressed size from the zip header. The buffer is
 * allocated at exactly that size, so a stream claiming to be larger fails
 * with ERR_MEMORY instead of overrunning.
 *
 * On failure nothing is written to *output and the arena allocation is left
 * where it is - the caller is expected to be inside a mark/release bracket,
 * which is how the xlsx reader already works. An arena has no free, so
 * releasing to a mark is the only correct cleanup, and doing it here would
 * discard allocations the caller made before this call. */
KhzSheetStatus khz_inflate_to_arena(KhzArena *arena,
                                    const void *input,
                                    size_t input_bytes,
                                    size_t expected_bytes,
                                    unsigned char **output,
                                    size_t *produced);

/* Runs the decoder against known-answer vectors built into the source.
 * Returns the number of failures, zero meaning all matched. */
int khz_inflate_selftest(void);

/* Always 1 in this build. Present so the ABI layer can report the decoder's
 * presence the same way it reports the ledger and the xlsx writer, rather
 * than the C# side inferring it from a version number. */
int khz_inflate_available(void);

#ifdef __cplusplus
}
#endif

#endif /* KHZ_INFLATE_H */
