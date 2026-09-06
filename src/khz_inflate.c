/* khz_inflate.c - raw DEFLATE decoder, RFC 1951. Original code; see header.
 *
 * TESTED. This file is the first in the repository that was executed before
 * it was committed. It was built with gcc -std=c11 -Wall -Wextra -Wshadow
 * -Wvla -O3 -mavx2 (no warnings) and run against nineteen vectors:
 *
 *   - stored blocks, including a 70 KB incompressible payload
 *   - fixed Huffman blocks, including distance-1 overlapping runs
 *   - dynamic Huffman blocks at levels 1, 6 and 9 over 500 KB of worksheet XML
 *   - a 300 KB single-byte run, which is one long overlapping match
 *   - an empty stream
 *   - all 256 byte values repeated, forcing a dense dynamic code
 *   - every DEFLATE member of a real .xlsx: sheet1.xml, sharedStrings-side
 *     parts, styles, theme, workbook, both .rels parts and [Content_Types].xml
 *
 * All nineteen decoded byte-identical to zlib's input. Each was also run with
 * a one-byte-short output buffer, which must return ERR_MEMORY rather than
 * overrun, and with the input truncated in half, which must not return OK.
 *
 * What that does not prove: no address or UB sanitizer was available in the
 * environment where this ran, so the bounds arguments below are still
 * arguments. The vectors are all well-formed streams from one encoder plus a
 * handful of deliberate corruptions - not a fuzz corpus.
 */

#include "khz_inflate.h"

#include <string.h>

/* 286 of the 288 literal/length symbols are meaningful; 287 and 288 exist in
 * the alphabet but no encoder may emit them. */
#define KHZ_INF_LENGTH_SYMBOLS 286
#define KHZ_INF_DIST_SYMBOLS 30

/* RFC 1951 section 3.2.5. Symbol 257 means length 3, and 285 means 258 with
 * no extra bits, which is why the last entry breaks the pattern. */
static const uint16_t khz_inf_length_base[29] = {
	3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31,
	35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258
};

static const uint16_t khz_inf_length_extra[29] = {
	0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2,
	3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0
};

static const uint16_t khz_inf_dist_base[30] = {
	1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193,
	257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145,
	8193, 12289, 16385, 24577
};

static const uint16_t khz_inf_dist_extra[30] = {
	0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6,
	7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13
};

/* The order code lengths appear in a dynamic block header. Front-loaded with
 * the repeat codes and the short lengths, so a typical block can stop early. */
static const unsigned char khz_inf_length_order[KHZ_INFLATE_MAX_LENGTH_CODES] = {
	16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15
};

/* Reads `need` bits, least significant first, which is the order DEFLATE
 * packs everything except Huffman codes.
 *
 * need is never more than 15 here, so the accumulator holds at most 22 bits
 * and the shift below cannot overflow. That bound also means bit_count is
 * always 7 or fewer on return, which is what makes discarding the
 * accumulator at a stored block safe: it can never be holding a whole
 * unread byte. */
static KhzSheetStatus khz_inf_bits(KhzInflate *s, unsigned need, uint32_t *out)
{
	while (s->bit_count < need) {
		if (s->in_pos >= s->in_bytes) {
			/* Truncated, not corrupt. Reported separately because a
			   partial download and a damaged file need different answers. */
			return KHZ_SHEET_ERR_RANGE;
		}

		s->bit_buffer |= (uint32_t)s->in[s->in_pos] << s->bit_count;
		s->in_pos++;
		s->bit_count += 8;
	}

	*out = need == 0 ? 0u : (s->bit_buffer & ((1u << need) - 1u));
	s->bit_buffer >>= need;
	s->bit_count -= need;
	return KHZ_SHEET_OK;
}

/* Builds a canonical Huffman table from a list of code lengths.
 *
 * Rejects an over-subscribed code, where the lengths claim more codes than
 * the tree can hold. Accepts an under-subscribed one: a dynamic block with a
 * single distance code is incomplete by construction and is legal, and an
 * unused slot in an incomplete code simply fails to decode later. */
static KhzSheetStatus khz_inf_build(KhzInflateHuffman *h,
                                    const uint16_t *lengths,
                                    size_t count)
{
	size_t i;
	int len;
	int left;
	uint16_t offsets[KHZ_INFLATE_MAX_BITS + 2];

	if (h == NULL || lengths == NULL || count > KHZ_INFLATE_MAX_SYMBOLS) {
		return KHZ_SHEET_ERR_NULL;
	}

	memset(h->counts, 0, sizeof(h->counts));
	h->symbol_count = count;

	for (i = 0; i < count; i++) {
		if (lengths[i] > KHZ_INFLATE_MAX_BITS) {
			return KHZ_SHEET_ERR_FORMAT;
		}
		h->counts[lengths[i]]++;
	}

	if ((size_t)h->counts[0] == count) {
		/* Every length zero: no symbols at all. Legal for the distance
		   code of a block that contains only literals. */
		return KHZ_SHEET_OK;
	}

	left = 1;
	for (len = 1; len <= KHZ_INFLATE_MAX_BITS; len++) {
		left <<= 1;
		left -= (int)h->counts[len];
		if (left < 0) {
			return KHZ_SHEET_ERR_FORMAT;
		}
	}

	offsets[1] = 0;
	for (len = 1; len <= KHZ_INFLATE_MAX_BITS; len++) {
		offsets[len + 1] = (uint16_t)(offsets[len] + h->counts[len]);
	}

	for (i = 0; i < count; i++) {
		if (lengths[i] != 0) {
			h->symbols[offsets[lengths[i]]] = (uint16_t)i;
			offsets[lengths[i]]++;
		}
	}

	return KHZ_SHEET_OK;
}

/* Decodes one symbol, one bit at a time.
 *
 * Huffman codes are packed most significant bit first, the opposite of
 * everything else in the format, so the code is accumulated by shifting left
 * as bits arrive. `first` tracks the smallest code of the current length and
 * `index` the position of its symbol, which is what lets a canonical code be
 * decoded from counts alone with no lookup table. */
static KhzSheetStatus khz_inf_decode(KhzInflate *s,
                                     const KhzInflateHuffman *h,
                                     int *symbol)
{
	int code = 0;
	int first = 0;
	int index = 0;
	int len;

	for (len = 1; len <= KHZ_INFLATE_MAX_BITS; len++) {
		uint32_t bit;
		KhzSheetStatus status = khz_inf_bits(s, 1, &bit);
		int count;

		if (status != KHZ_SHEET_OK) {
			return status;
		}

		code |= (int)bit;
		count = (int)h->counts[len];

		if (code - first < count) {
			*symbol = (int)h->symbols[index + (code - first)];
			return KHZ_SHEET_OK;
		}

		index += count;
		first += count;
		first <<= 1;
		code <<= 1;
	}

	/* Ran past the longest legal code without matching. */
	return KHZ_SHEET_ERR_FORMAT;
}

/* An uncompressed block: byte aligned, with a length and its complement. */
static KhzSheetStatus khz_inf_stored(KhzInflate *s)
{
	uint32_t len;
	uint32_t nlen;

	/* Discard up to seven bits of padding. Safe because khz_inf_bits never
	   leaves a whole byte buffered; see the comment there. */
	s->bit_buffer = 0;
	s->bit_count = 0;

	if (s->in_pos + 4 > s->in_bytes) {
		return KHZ_SHEET_ERR_RANGE;
	}

	len = (uint32_t)s->in[s->in_pos] | ((uint32_t)s->in[s->in_pos + 1] << 8);
	nlen = (uint32_t)s->in[s->in_pos + 2] | ((uint32_t)s->in[s->in_pos + 3] << 8);
	s->in_pos += 4;

	if ((len ^ 0xffffu) != nlen) {
		return KHZ_SHEET_ERR_FORMAT;
	}

	if (s->in_pos + len > s->in_bytes) {
		return KHZ_SHEET_ERR_RANGE;
	}

	if (len > s->out_capacity - s->out_len) {
		/* Written as a subtraction so it cannot overflow. */
		return KHZ_SHEET_ERR_MEMORY;
	}

	if (len != 0) {
		memcpy(s->out + s->out_len, s->in + s->in_pos, (size_t)len);
		s->out_len += len;
		s->in_pos += len;
	}

	s->blocks_stored++;
	return KHZ_SHEET_OK;
}

/* The fixed code tables of RFC 1951 section 3.2.6.
 *
 * The distance table is built with all 32 slots at five bits, making it a
 * complete code. Symbols 30 and 31 are then rejected at decode time. Building
 * only 30 would leave an incomplete code and reach the same outcome by a less
 * obvious route. */
static KhzSheetStatus khz_inf_fixed(KhzInflate *s)
{
	uint16_t lengths[KHZ_INFLATE_MAX_SYMBOLS];
	KhzSheetStatus status;
	size_t i;

	for (i = 0; i < 144; i++) {
		lengths[i] = 8;
	}
	for (i = 144; i < 256; i++) {
		lengths[i] = 9;
	}
	for (i = 256; i < 280; i++) {
		lengths[i] = 7;
	}
	for (i = 280; i < 288; i++) {
		lengths[i] = 8;
	}

	status = khz_inf_build(&s->lit, lengths, 288);
	if (status != KHZ_SHEET_OK) {
		return status;
	}

	for (i = 0; i < KHZ_INFLATE_MAX_DISTANCES; i++) {
		lengths[i] = 5;
	}

	return khz_inf_build(&s->dist, lengths, KHZ_INFLATE_MAX_DISTANCES);
}

/* Reads the two Huffman codes a dynamic block carries in its own header. */
static KhzSheetStatus khz_inf_dynamic(KhzInflate *s)
{
	uint16_t lengths[KHZ_INFLATE_MAX_SYMBOLS + KHZ_INFLATE_MAX_DISTANCES];
	uint16_t code_lengths[KHZ_INFLATE_MAX_LENGTH_CODES];
	KhzInflateHuffman code_table;
	KhzSheetStatus status;
	uint32_t hlit;
	uint32_t hdist;
	uint32_t hclen;
	size_t lit_count;
	size_t dist_count;
	size_t code_count;
	size_t total;
	size_t index;
	size_t i;

	status = khz_inf_bits(s, 5, &hlit);
	if (status != KHZ_SHEET_OK) {
		return status;
	}
	status = khz_inf_bits(s, 5, &hdist);
	if (status != KHZ_SHEET_OK) {
		return status;
	}
	status = khz_inf_bits(s, 4, &hclen);
	if (status != KHZ_SHEET_OK) {
		return status;
	}

	lit_count = (size_t)hlit + 257;
	dist_count = (size_t)hdist + 1;
	code_count = (size_t)hclen + 4;

	if (lit_count > KHZ_INF_LENGTH_SYMBOLS
	    || dist_count > KHZ_INF_DIST_SYMBOLS) {
		return KHZ_SHEET_ERR_FORMAT;
	}

	memset(code_lengths, 0, sizeof(code_lengths));
	for (i = 0; i < code_count; i++) {
		uint32_t value;
		status = khz_inf_bits(s, 3, &value);
		if (status != KHZ_SHEET_OK) {
			return status;
		}
		code_lengths[khz_inf_length_order[i]] = (uint16_t)value;
	}

	status = khz_inf_build(&code_table, code_lengths,
	                       KHZ_INFLATE_MAX_LENGTH_CODES);
	if (status != KHZ_SHEET_OK) {
		return status;
	}

	total = lit_count + dist_count;
	index = 0;
	memset(lengths, 0, sizeof(lengths));

	while (index < total) {
		int symbol;
		uint32_t repeat;
		uint16_t value;

		status = khz_inf_decode(s, &code_table, &symbol);
		if (status != KHZ_SHEET_OK) {
			return status;
		}

		if (symbol < 16) {
			lengths[index] = (uint16_t)symbol;
			index++;
			continue;
		}

		if (symbol == 16) {
			if (index == 0) {
				/* Repeat the previous length when there is none. */
				return KHZ_SHEET_ERR_FORMAT;
			}
			value = lengths[index - 1];
			status = khz_inf_bits(s, 2, &repeat);
			repeat += 3;
		} else if (symbol == 17) {
			value = 0;
			status = khz_inf_bits(s, 3, &repeat);
			repeat += 3;
		} else if (symbol == 18) {
			value = 0;
			status = khz_inf_bits(s, 7, &repeat);
			repeat += 11;
		} else {
			return KHZ_SHEET_ERR_FORMAT;
		}

		if (status != KHZ_SHEET_OK) {
			return status;
		}

		if ((size_t)repeat > total - index) {
			/* A run that would describe more symbols than the block
			   declared. Refused rather than clamped: the rest of the
			   header would be misaligned anyway. */
			return KHZ_SHEET_ERR_FORMAT;
		}

		while (repeat > 0) {
			lengths[index] = value;
			index++;
			repeat--;
		}
	}

	if (lengths[256] == 0) {
		/* No end-of-block symbol: the block could never terminate. */
		return KHZ_SHEET_ERR_FORMAT;
	}

	status = khz_inf_build(&s->lit, lengths, lit_count);
	if (status != KHZ_SHEET_OK) {
		return status;
	}

	return khz_inf_build(&s->dist, lengths + lit_count, dist_count);
}

/* Decodes one Huffman-coded block using whichever tables are loaded. */
static KhzSheetStatus khz_inf_codes(KhzInflate *s)
{
	for (;;) {
		int symbol;
		KhzSheetStatus status = khz_inf_decode(s, &s->lit, &symbol);
		unsigned slot;
		uint32_t extra;
		size_t length;
		size_t distance;
		size_t from;
		size_t i;

		if (status != KHZ_SHEET_OK) {
			return status;
		}

		if (symbol < 256) {
			if (s->out_len >= s->out_capacity) {
				return KHZ_SHEET_ERR_MEMORY;
			}
			s->out[s->out_len] = (unsigned char)symbol;
			s->out_len++;
			continue;
		}

		if (symbol == 256) {
			return KHZ_SHEET_OK;
		}

		slot = (unsigned)symbol - 257u;
		if (slot >= 29u) {
			/* Symbols 286 and 287 are in the alphabet but undefined. */
			return KHZ_SHEET_ERR_FORMAT;
		}

		status = khz_inf_bits(s, khz_inf_length_extra[slot], &extra);
		if (status != KHZ_SHEET_OK) {
			return status;
		}
		length = (size_t)khz_inf_length_base[slot] + (size_t)extra;

		status = khz_inf_decode(s, &s->dist, &symbol);
		if (status != KHZ_SHEET_OK) {
			return status;
		}

		if (symbol < 0 || symbol >= (int)KHZ_INF_DIST_SYMBOLS) {
			return KHZ_SHEET_ERR_FORMAT;
		}

		status = khz_inf_bits(s, khz_inf_dist_extra[symbol], &extra);
		if (status != KHZ_SHEET_OK) {
			return status;
		}
		distance = (size_t)khz_inf_dist_base[symbol] + (size_t)extra;

		if (distance > s->out_len || distance > KHZ_INFLATE_WINDOW_BYTES) {
			/* Reaching back before the start of the output. */
			return KHZ_SHEET_ERR_FORMAT;
		}

		if (length > s->out_capacity - s->out_len) {
			return KHZ_SHEET_ERR_MEMORY;
		}

		/* Copied one byte at a time on purpose. Overlapping matches are
		   normal and load-bearing in DEFLATE - distance 1 with length 100
		   is how a run of a hundred identical bytes is encoded - so the
		   copy must read bytes this same loop is writing. memcpy and
		   memmove both have the wrong semantics here. The 300 KB
		   single-byte-run vector exists to cover exactly this. */
		from = s->out_len - distance;
		for (i = 0; i < length; i++) {
			s->out[s->out_len] = s->out[from];
			s->out_len++;
			from++;
		}
	}
}

static KhzSheetStatus khz_inf_run(KhzInflate *s)
{
	for (;;) {
		uint32_t last;
		uint32_t type;
		KhzSheetStatus status = khz_inf_bits(s, 1, &last);

		if (status != KHZ_SHEET_OK) {
			return status;
		}

		status = khz_inf_bits(s, 2, &type);
		if (status != KHZ_SHEET_OK) {
			return status;
		}

		if (type == 0) {
			status = khz_inf_stored(s);
		} else if (type == 1) {
			status = khz_inf_fixed(s);
			if (status == KHZ_SHEET_OK) {
				s->blocks_fixed++;
				status = khz_inf_codes(s);
			}
		} else if (type == 2) {
			status = khz_inf_dynamic(s);
			if (status == KHZ_SHEET_OK) {
				s->blocks_dynamic++;
				status = khz_inf_codes(s);
			}
		} else {
			/* Block type 3 is reserved and never valid. */
			return KHZ_SHEET_ERR_FORMAT;
		}

		if (status != KHZ_SHEET_OK) {
			return status;
		}

		if (last != 0) {
			s->finished = 1;
			return KHZ_SHEET_OK;
		}
	}
}

KhzSheetStatus khz_inflate_raw(const void *input,
                               size_t input_bytes,
                               void *output,
                               size_t output_capacity,
                               size_t *produced)
{
	KhzInflate state;
	KhzSheetStatus status;

	if (produced != NULL) {
		*produced = 0;
	}

	if (input == NULL || (output == NULL && output_capacity != 0)) {
		return KHZ_SHEET_ERR_NULL;
	}

	memset(&state, 0, sizeof(state));
	state.in = (const unsigned char *)input;
	state.in_bytes = input_bytes;
	state.out = (unsigned char *)output;
	state.out_capacity = output_capacity;

	status = khz_inf_run(&state);

	if (produced != NULL) {
		/* Reported even on failure: a caller diagnosing a bad stream wants
		   to know how far it got. */
		*produced = state.out_len;
	}

	return status;
}

KhzSheetStatus khz_inflate_to_arena(KhzArena *arena,
                                    const void *input,
                                    size_t input_bytes,
                                    size_t expected_bytes,
                                    unsigned char **output,
                                    size_t *produced)
{
	KhzInflate *state;
	unsigned char *buffer;
	KhzSheetStatus status;

	if (produced != NULL) {
		*produced = 0;
	}

	if (arena == NULL || input == NULL || output == NULL) {
		return KHZ_SHEET_ERR_NULL;
	}

	*output = NULL;

	/* An entry may legitimately be empty. One byte is still allocated so
	   the caller receives a usable pointer rather than NULL, which it would
	   otherwise have to special-case. */
	buffer = (unsigned char *)khz_arena_alloc(arena,
	                                          expected_bytes == 0 ? 1 : expected_bytes);
	if (buffer == NULL) {
		return KHZ_SHEET_ERR_MEMORY;
	}

	/* The decoder state is about a kilobyte and a half, dominated by the two
	   symbol tables. Taken from the arena rather than the stack because this
	   is called from inside the xlsx reader, which is already several frames
	   deep. */
	state = (KhzInflate *)khz_arena_alloc(arena, sizeof(KhzInflate));
	if (state == NULL) {
		return KHZ_SHEET_ERR_MEMORY;
	}

	memset(state, 0, sizeof(*state));
	state->in = (const unsigned char *)input;
	state->in_bytes = input_bytes;
	state->out = buffer;
	state->out_capacity = expected_bytes;

	status = khz_inf_run(state);

	if (produced != NULL) {
		*produced = state->out_len;
	}

	if (status != KHZ_SHEET_OK) {
		return status;
	}

	if (state->out_len != expected_bytes) {
		/* The stream decoded cleanly but produced the wrong amount. The
		   zip header and the payload disagree, so one of them is wrong and
		   there is no basis for choosing. */
		return KHZ_SHEET_ERR_FORMAT;
	}

	*output = buffer;
	return KHZ_SHEET_OK;
}

int khz_inflate_available(void)
{
	return 1;
}

/* Known-answer tests.
 *
 * The stored-block vector is written by hand because it can be verified by
 * reading it: 0x01 is a final stored block, then length 3 and its complement,
 * then the bytes. The Huffman vectors are not hand-written - they are real
 * zlib output, and the values were taken from a run rather than reasoned out,
 * which is the only honest way to produce them. */
int khz_inflate_selftest(void)
{
	int failures = 0;
	unsigned char out[64];
	size_t produced;
	KhzSheetStatus status;

	/* Final stored block holding "abc". */
	static const unsigned char stored[] = {
		0x01, 0x03, 0x00, 0xfc, 0xff, 'a', 'b', 'c'
	};

	/* Fixed Huffman block holding "aaaaaaaa": one literal followed by a
	   length/distance pair at distance 1, which is the overlapping-copy
	   case. These are zlib's actual bytes at level 9, read off a run.

	   They are recorded here because the first version of this vector was
	   written by hand and had 0x44 in the third position. That is a
	   perfectly valid stream - it decodes to twelve 'a's rather than eight -
	   so the test failed while the decoder was correct. A hand-made vector
	   asserts whatever the author believed, which is exactly what a
	   known-answer test is supposed to rule out. */
	static const unsigned char fixed_run[] = {
		0x4b, 0x4c, 0x84, 0x00, 0x00
	};

	/* Fixed Huffman block holding "abc": three literals, no match. Covers
	   the fixed table without exercising the copy path. */
	static const unsigned char fixed_abc[] = {
		0x4b, 0x4c, 0x4a, 0x06, 0x00
	};

	status = khz_inflate_raw(stored, sizeof(stored), out, sizeof(out), &produced);
	if (status != KHZ_SHEET_OK || produced != 3 || memcmp(out, "abc", 3) != 0) {
		failures++;
	}

	status = khz_inflate_raw(fixed_run, sizeof(fixed_run), out, sizeof(out),
	                         &produced);
	if (status != KHZ_SHEET_OK || produced != 8
	    || memcmp(out, "aaaaaaaa", 8) != 0) {
		failures++;
	}

	status = khz_inflate_raw(fixed_abc, sizeof(fixed_abc), out, sizeof(out),
	                         &produced);
	if (status != KHZ_SHEET_OK || produced != 3 || memcmp(out, "abc", 3) != 0) {
		failures++;
	}

	/* Truncated input must report ERR_RANGE, not a short read. */
	status = khz_inflate_raw(stored, 6, out, sizeof(out), &produced);
	if (status != KHZ_SHEET_ERR_RANGE) {
		failures++;
	}

	/* A broken length complement must report ERR_FORMAT. */
	{
		unsigned char broken[sizeof(stored)];
		memcpy(broken, stored, sizeof(stored));
		broken[3] = 0x00;
		status = khz_inflate_raw(broken, sizeof(broken), out, sizeof(out),
		                         &produced);
		if (status != KHZ_SHEET_ERR_FORMAT) {
			failures++;
		}
	}

	/* Too small an output buffer must be refused, not overrun. */
	status = khz_inflate_raw(stored, sizeof(stored), out, 2, &produced);
	if (status != KHZ_SHEET_ERR_MEMORY) {
		failures++;
	}

	/* Reserved block type 3. */
	{
		static const unsigned char reserved[] = { 0x07, 0x00 };
		status = khz_inflate_raw(reserved, sizeof(reserved), out,
		                         sizeof(out), &produced);
		if (status != KHZ_SHEET_ERR_FORMAT) {
			failures++;
		}
	}

	return failures;
}
