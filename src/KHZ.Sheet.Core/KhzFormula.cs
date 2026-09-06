using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace KHZ.Sheet.Core
{
	/// <summary>Operations the C formula IR understands.</summary>
	public enum KhzFormulaOp
	{
		Const = 0,
		Reference = 1,
		Range = 2,
		Add = 3,
		Subtract = 4,
		Multiply = 5,
		Divide = 6,
		Negate = 7,
		Sum = 8,
		Average = 9,
		Min = 10,
		Max = 11,
	}

	/// <summary>An exact rational, mirroring KhzRational.</summary>
	[StructLayout(LayoutKind.Sequential)]
	public struct KhzRational
	{
		public long Numerator;
		public long Denominator;
	}

	/// <summary>Mirrors KhzFormulaResult: either a value or a cell error.</summary>
	[StructLayout(LayoutKind.Sequential)]
	public struct KhzFormulaResult
	{
		public uint Kind;
		public uint Error;
		public KhzRational Value;

		public bool IsError
		{
			get { return Kind == 4u; }
		}
	}

	/// <summary>Mirrors KhzFormulaParseError.</summary>
	[StructLayout(LayoutKind.Sequential)]
	internal unsafe struct KhzFormulaParseErrorNative
	{
		public nuint Offset;
		public fixed byte Expected[32];
	}

	/// <summary>Where a formula failed to parse, and what was wanted there.</summary>
	public readonly struct FormulaParseFailure
	{
		public FormulaParseFailure(ulong offset, string expected)
		{
			Offset = offset;
			Expected = expected;
		}

		public ulong Offset { get; }

		public string Expected { get; }
	}

	internal static unsafe class KhzNativeFormula
	{
		internal const string Lib = "khz_sheet";

		/// <summary>
		/// Size of the stack window reserved for a KhzFormula control block.
		///
		/// This is not a claim about sizeof(KhzFormula). It is the amount of
		/// stack this file is willing to set aside, and it exists only because
		/// stackalloc needs a size before the native size can be queried. The
		/// real size comes from khz_abi_formula_bytes() and is checked against
		/// this window before anything is written into it.
		/// </summary>
		internal const int StackBlockBytes = 512;

		private static int cachedFormulaBytes;

		[DllImport(Lib, EntryPoint = "khz_abi_formula_bytes", CallingConvention = CallingConvention.Cdecl)]
		private static extern nuint AbiFormulaBytes();

		[DllImport(Lib, EntryPoint = "khz_abi_formula_node_bytes", CallingConvention = CallingConvention.Cdecl)]
		private static extern nuint AbiFormulaNodeBytes();

		/// <summary>sizeof(KhzFormulaNode) as compiled. Diagnostic only.</summary>
		internal static ulong NodeBytes()
		{
			return (ulong)AbiFormulaNodeBytes();
		}

		/// <summary>
		/// Resolves sizeof(KhzFormula) from the loaded library and confirms it
		/// fits the reserved stack window.
		///
		/// Phase 93 hardcoded 256 bytes here and hoped. That was a single
		/// assumption standing in for pointer width, size_t width and struct
		/// padding, and if the native struct had been larger the builder would
		/// have written past the block and corrupted the stack with nothing
		/// reporting it. The number is now the compiler's, and a struct that
		/// outgrows the window is a returned status rather than a silent
		/// overwrite.
		/// </summary>
		internal static SheetStatus EnsureBlockSize(out int bytes)
		{
			int cached = Volatile.Read(ref cachedFormulaBytes);

			if (cached > 0)
			{
				bytes = cached;
				return SheetStatus.Ok;
			}

			bytes = 0;

			ulong reported;

			try
			{
				reported = (ulong)AbiFormulaBytes();
			}
			catch (DllNotFoundException)
			{
				/* The native library is genuinely absent. That is an environment
				   failure rather than an expected formula outcome, so it is one of
				   the few places an exception is allowed to be caught and turned
				   into a status instead of propagating. */
				return SheetStatus.ErrState;
			}
			catch (EntryPointNotFoundException)
			{
				/* An older library without the Phase 94 ABI queries. Refusing is
				   correct: falling back to 256 would reinstate exactly the
				   assumption this method exists to remove. */
				return SheetStatus.ErrUnsupported;
			}

			if (reported == 0UL)
			{
				return SheetStatus.ErrState;
			}
			if (reported > (ulong)StackBlockBytes)
			{
				/* KhzFormula outgrew the reserved window. Raising StackBlockBytes
				   and rebuilding is the fix; guessing is not. */
				return SheetStatus.ErrLimit;
			}

			cached = (int)reported;
			Volatile.Write(ref cachedFormulaBytes, cached);

			bytes = cached;
			return SheetStatus.Ok;
		}

		[DllImport(Lib, EntryPoint = "khz_formula_begin", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int Begin(byte* formula, IntPtr arena, uint col, uint row);

		[DllImport(Lib, EntryPoint = "khz_formula_abandon", CallingConvention = CallingConvention.Cdecl)]
		internal static extern void Abandon(byte* formula);

		[DllImport(Lib, EntryPoint = "khz_formula_const", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int Const(byte* formula, KhzRational value, IntPtr* node);

		[DllImport(Lib, EntryPoint = "khz_formula_ref", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int Ref(byte* formula, uint col, uint row, IntPtr* node);

		[DllImport(Lib, EntryPoint = "khz_formula_range", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int Range(byte* formula, uint col0, uint row0, uint col1, uint row1, IntPtr* node);

		[DllImport(Lib, EntryPoint = "khz_formula_node", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int Node(byte* formula, int op, IntPtr* children, nuint childCount, IntPtr* node);

		[DllImport(Lib, EntryPoint = "khz_formula_set_root", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int SetRoot(byte* formula, IntPtr root);

		[DllImport(Lib, EntryPoint = "khz_formula_parse_ref", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int ParseRef(byte* text, nuint len, uint* col, uint* row);

		[DllImport(Lib, EntryPoint = "khz_formula_eval", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int Eval(IntPtr sheet, byte* formula, KhzFormulaResult* result);

		[DllImport(Lib, EntryPoint = "khz_formula_declare_dependencies", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int DeclareDependencies(IntPtr sheet, byte* formula, ulong* declared);

		[DllImport(Lib, EntryPoint = "khz_formula_set", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int Set(IntPtr sheet, uint col, uint row, byte* source, nuint len,
		                               KhzFormulaParseErrorNative* error);

		[DllImport(Lib, EntryPoint = "khz_formula_recalc", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int Recalc(IntPtr sheet, ulong* evaluated);
	}

	/// <summary>
	/// Lowers the managed syntax tree onto the C formula IR.
	///
	/// The boundary is Option A: C owns the IR, evaluates it, and commits the
	/// result onto the proof chain. Nothing here computes a value. This class
	/// only translates shapes, so there is exactly one evaluator in the system
	/// and no possibility of the managed and native answers disagreeing.
	///
	/// Every method returns SheetStatus. No exception is thrown for a formula a
	/// user could plausibly type: an unsupported operator, a bad reference and a
	/// parse failure are all return codes.
	/// </summary>
	public static unsafe class KhzFormula
	{
		/// <summary>
		/// sizeof(KhzFormula) as reported by the loaded library, or zero when it
		/// could not be resolved. Exposed so a caller can log what it bound to.
		/// </summary>
		public static ulong NativeFormulaBytes
		{
			get
			{
				int bytes;
				return KhzNativeFormula.EnsureBlockSize(out bytes) == SheetStatus.Ok
					? (ulong)bytes
					: 0UL;
			}
		}

		/// <summary>
		/// Parses and installs a formula from its source text, letting the C
		/// parser do the work. This is the preferred path: the text is the only
		/// representation both layers agree on exactly.
		/// </summary>
		public static SheetStatus SetFormula(IntPtr sheet, uint col, uint row, string source,
		                                     out FormulaParseFailure failure)
		{
			failure = new FormulaParseFailure(0UL, string.Empty);

			if (sheet == IntPtr.Zero)
			{
				return SheetStatus.ErrNull;
			}
			if (string.IsNullOrEmpty(source))
			{
				return SheetStatus.ErrRange;
			}

			byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(source);
			KhzFormulaParseErrorNative error = default;
			int status;

			fixed (byte* text = utf8)
			{
				status = KhzNativeFormula.Set(sheet, col, row, text, (nuint)utf8.Length, &error);
			}

			if (status != 0)
			{
				failure = ReadFailure(ref error);
			}

			return (SheetStatus)status;
		}

		/// <summary>Recalculates the formula cells that are marked dirty.</summary>
		public static SheetStatus Recalculate(IntPtr sheet, out ulong evaluated)
		{
			ulong count = 0UL;
			int status;

			evaluated = 0UL;

			if (sheet == IntPtr.Zero)
			{
				return SheetStatus.ErrNull;
			}

			status = KhzNativeFormula.Recalc(sheet, &count);

			if (status == 0)
			{
				evaluated = count;
			}

			/* ErrCycle here means a circular reference and nothing was written. */
			return (SheetStatus)status;
		}

		/// <summary>
		/// Lowers a managed tree into native IR, declares its dependencies, then
		/// releases the tree. Used when the managed parser has already run, for
		/// instance over a formula read out of a workbook.
		/// </summary>
		public static SheetStatus DeclareDependencies(IntPtr sheet, IntPtr arena, uint col, uint row,
		                                              FormulaNode node, out ulong declared)
		{
			declared = 0UL;

			if (sheet == IntPtr.Zero || arena == IntPtr.Zero || node == null)
			{
				return SheetStatus.ErrNull;
			}

			int blockBytes;
			SheetStatus sized = KhzNativeFormula.EnsureBlockSize(out blockBytes);

			if (sized != SheetStatus.Ok)
			{
				return sized;
			}

			byte* block = stackalloc byte[KhzNativeFormula.StackBlockBytes];
			new Span<byte>(block, KhzNativeFormula.StackBlockBytes).Clear();

			int status = KhzNativeFormula.Begin(block, arena, col, row);
			if (status != 0)
			{
				return (SheetStatus)status;
			}

			IntPtr root = IntPtr.Zero;
			SheetStatus lowered = Lower(block, node, 0, ref root);

			if (lowered != SheetStatus.Ok)
			{
				KhzNativeFormula.Abandon(block);
				return lowered;
			}

			status = KhzNativeFormula.SetRoot(block, root);
			if (status != 0)
			{
				KhzNativeFormula.Abandon(block);
				return (SheetStatus)status;
			}

			ulong count = 0UL;
			status = KhzNativeFormula.DeclareDependencies(sheet, block, &count);

			/* The tree is scratch on the native arena. Abandoning returns it. */
			KhzNativeFormula.Abandon(block);

			if (status == 0)
			{
				declared = count;
			}

			return (SheetStatus)status;
		}

		/// <summary>Lowers a managed tree and evaluates it without storing it.</summary>
		public static SheetStatus Evaluate(IntPtr sheet, IntPtr arena, uint col, uint row,
		                                   FormulaNode node, out KhzFormulaResult result)
		{
			result = default;

			if (sheet == IntPtr.Zero || arena == IntPtr.Zero || node == null)
			{
				return SheetStatus.ErrNull;
			}

			int blockBytes;
			SheetStatus sized = KhzNativeFormula.EnsureBlockSize(out blockBytes);

			if (sized != SheetStatus.Ok)
			{
				return sized;
			}

			byte* block = stackalloc byte[KhzNativeFormula.StackBlockBytes];
			new Span<byte>(block, KhzNativeFormula.StackBlockBytes).Clear();

			int status = KhzNativeFormula.Begin(block, arena, col, row);
			if (status != 0)
			{
				return (SheetStatus)status;
			}

			IntPtr root = IntPtr.Zero;
			SheetStatus lowered = Lower(block, node, 0, ref root);

			if (lowered != SheetStatus.Ok)
			{
				KhzNativeFormula.Abandon(block);
				return lowered;
			}

			status = KhzNativeFormula.SetRoot(block, root);
			if (status == 0)
			{
				KhzFormulaResult native;
				status = KhzNativeFormula.Eval(sheet, block, &native);

				if (status == 0)
				{
					result = native;
				}
			}

			KhzNativeFormula.Abandon(block);

			return (SheetStatus)status;
		}

		private const int MaxDepth = 64;

		private static SheetStatus Lower(byte* block, FormulaNode node, int depth, ref IntPtr result)
		{
			if (node == null)
			{
				return SheetStatus.ErrNull;
			}
			if (depth > MaxDepth)
			{
				return SheetStatus.ErrLimit;
			}

			switch (node)
			{
				case NumberNode number:
					/* The node is passed whole, not just its double, so the exact
					   literal text can be used when it is present. */
					return LowerNumber(block, number, ref result);

				case GroupNode group:
					/* Parentheses are a text artefact; the tree already has the
					   grouping in its shape. */
					return Lower(block, group.Inner, depth + 1, ref result);

				case ReferenceNode reference:
					return LowerReference(block, reference.Text, ref result);

				case UnaryNode unary:
					return LowerUnary(block, unary, depth, ref result);

				case PostfixNode postfix:
					return LowerPostfix(block, postfix, depth, ref result);

				case BinaryNode binary:
					return LowerBinary(block, binary, depth, ref result);

				case FunctionNode function:
					return LowerFunction(block, function, depth, ref result);

				default:
					/* Text, Boolean, Error, Name and Missing have no IR operation.
					   They are refused rather than approximated, because a text
					   literal silently lowered to zero is a wrong answer that
					   looks like a right one. */
					return SheetStatus.ErrUnsupported;
			}
		}

		/// <summary>
		/// Lowers a numeric literal to an exact rational constant.
		///
		/// Two paths, and which one is taken depends only on whether the node
		/// carries its source text:
		///
		/// With RawText, the digits are converted straight to num/den and no
		/// double is consulted. This is the correct path and, since Push H, the
		/// one every parsed formula takes.
		///
		/// Without it, FromDouble reconstructs a rational from the double. That
		/// is a repair for information already lost, kept only because the
		/// one-argument NumberNode constructor still exists and a caller
		/// building a tree by hand may use it.
		/// </summary>
		private static SheetStatus LowerNumber(byte* block, NumberNode number, ref IntPtr result)
		{
			if (number == null)
			{
				return SheetStatus.ErrNull;
			}

			KhzRational rational;

			/* Bound to a local so the null check is one the compiler can follow,
			   and so the value cannot change between the test and the use. */
			string? raw = number.RawText;

			if (raw != null)
			{
				SheetStatus exact = TryExactRational(raw, out rational);

				if (exact != SheetStatus.Ok)
				{
					/* No silent fall back to the double path. A literal that has
					   no exact int64 rational is refused; approximating it here
					   would defeat the entire point of carrying the text. */
					return exact;
				}
			}
			else
			{
				SheetStatus repaired = FromDouble(number.Value, out rational);

				if (repaired != SheetStatus.Ok)
				{
					return repaired;
				}
			}

			IntPtr node = IntPtr.Zero;
			int status = KhzNativeFormula.Const(block, rational, &node);

			if (status == 0)
			{
				result = node;
			}

			return (SheetStatus)status;
		}

		/// <summary>
		/// Converts a decimal literal, exactly as written, into an int64
		/// rational. No floating point is involved at any point.
		///
		/// The grammar accepted is the lexer's: optional sign, digits with an
		/// optional single decimal point, optional e or E exponent with its own
		/// optional sign. Anything else is ErrFormat rather than a partial
		/// conversion of the part that happened to parse.
		///
		/// The value is mantissa * 10^(exponent - fractionDigits). A positive
		/// net exponent scales the numerator; a negative one becomes the
		/// denominator. Both are checked before every multiply, so an overflow
		/// is ErrOverflow and never a wrapped value.
		///
		/// The result is reduced by gcd, which is what makes trailing zeros
		/// irrelevant: 0.10 and 0.1 both arrive as 1/10, and 50.00 as 50/1.
		/// Reduction also widens the range that fits, since 0.5000 reduces to
		/// 1/2 rather than needing a denominator of 10000.
		///
		/// Exponents are handled rather than refused because they are exact:
		/// 1e3 is 1000/1 and 2.5E-4 is 1/4000. There is no reason to reject a
		/// value that has an exact representation.
		/// </summary>
		private static SheetStatus TryExactRational(string text, out KhzRational rational)
		{
			rational = default;

			if (string.IsNullOrEmpty(text))
			{
				return SheetStatus.ErrFormat;
			}

			int i = 0;
			int n = text.Length;
			bool negative = false;

			/* The lexer does not put a sign on a number token - a leading minus
			   is a UnaryNode - but accepting one here costs nothing and makes
			   the method correct for a hand-built node too. */
			if (text[i] == '+' || text[i] == '-')
			{
				negative = text[i] == '-';
				i++;
			}

			ulong mantissa = 0UL;
			int digits = 0;
			int fractionDigits = 0;
			bool seenPoint = false;

			while (i < n)
			{
				char c = text[i];

				if (c == '.')
				{
					if (seenPoint)
					{
						return SheetStatus.ErrFormat;
					}

					seenPoint = true;
					i++;
					continue;
				}

				if (c < '0' || c > '9')
				{
					break;
				}

				digits++;

				/* Leading zeros are skipped rather than multiplied through, so
				   0.0000000000000000000001 does not overflow the mantissa on
				   its way to failing on the denominator. */
				if (mantissa != 0UL || c != '0')
				{
					ulong digit = (ulong)(c - '0');

					if (mantissa > (ulong.MaxValue - digit) / 10UL)
					{
						return SheetStatus.ErrOverflow;
					}

					mantissa = (mantissa * 10UL) + digit;
				}

				if (seenPoint)
				{
					fractionDigits++;
				}

				i++;
			}

			if (digits == 0)
			{
				return SheetStatus.ErrFormat;
			}

			int exponent = 0;

			if (i < n && (text[i] == 'e' || text[i] == 'E'))
			{
				i++;

				bool exponentNegative = false;

				if (i < n && (text[i] == '+' || text[i] == '-'))
				{
					exponentNegative = text[i] == '-';
					i++;
				}

				int exponentDigits = 0;

				while (i < n && text[i] >= '0' && text[i] <= '9')
				{
					int digit = text[i] - '0';

					/* An exponent beyond a few hundred cannot produce a value
					   that fits either way, but it is bounded here so the
					   accumulator itself cannot overflow. */
					if (exponent > (int.MaxValue - digit) / 10)
					{
						return SheetStatus.ErrOverflow;
					}

					exponent = (exponent * 10) + digit;
					exponentDigits++;
					i++;
				}

				if (exponentDigits == 0)
				{
					return SheetStatus.ErrFormat;
				}

				if (exponentNegative)
				{
					exponent = -exponent;
				}
			}

			/* Trailing junk means the whole literal is rejected. Converting the
			   prefix would accept 1.2.3 as 1.2. */
			if (i != n)
			{
				return SheetStatus.ErrFormat;
			}

			if (mantissa > (ulong)long.MaxValue)
			{
				return SheetStatus.ErrOverflow;
			}

			long numerator = (long)mantissa;
			long denominator = 1L;
			int netExponent = exponent - fractionDigits;

			if (netExponent > 0)
			{
				for (int k = 0; k < netExponent; ++k)
				{
					if (numerator > long.MaxValue / 10L)
					{
						return SheetStatus.ErrOverflow;
					}

					numerator *= 10L;
				}
			}
			else if (netExponent < 0)
			{
				int scale = -netExponent;

				for (int k = 0; k < scale; ++k)
				{
					if (denominator > long.MaxValue / 10L)
					{
						/* The value has no exact int64 rational. 1e-23 is a real
						   number the format can express and this core cannot
						   hold, so it is refused rather than rounded. */
						return SheetStatus.ErrOverflow;
					}

					denominator *= 10L;
				}

				long divisor = Gcd(numerator, denominator);

				if (divisor > 1L)
				{
					numerator /= divisor;
					denominator /= divisor;
				}
			}

			rational.Numerator = negative ? -numerator : numerator;
			rational.Denominator = denominator;

			return SheetStatus.Ok;
		}

		/// <summary>
		/// Greatest common divisor of a non-negative numerator and a positive
		/// denominator. Euclid, on unsigned values so no intermediate can be
		/// negative. Gcd(0, d) is d, which reduces a zero numerator to 0/1.
		/// </summary>
		private static long Gcd(long a, long b)
		{
			ulong x = (ulong)a;
			ulong y = (ulong)b;

			while (y != 0UL)
			{
				ulong t = x % y;
				x = y;
				y = t;
			}

			return (long)x;
		}

		/// <summary>
		/// Reconstructs a rational from a double, for nodes that carry no exact
		/// text.
		///
		/// This is a repair, not a conversion. By the time a literal is a double
		/// the value the user typed is gone: 0.1 is held as
		/// 3602879701896397/36028797018963968, and casting that directly would
		/// be exact but wrong. Formatting with "R" and re-parsing as decimal
		/// recovers the shortest round-trippable decimal instead, so 0.1 comes
		/// back as 1/10 - correct for the common cases and an inference in
		/// general.
		///
		/// Nothing produced by FormulaParser reaches here any more. It remains
		/// only for trees built through the one-argument NumberNode constructor.
		/// </summary>
		private static SheetStatus FromDouble(double value, out KhzRational rational)
		{
			rational = default;

			if (double.IsNaN(value) || double.IsInfinity(value))
			{
				return SheetStatus.ErrOverflow;
			}

			decimal exact;

			try
			{
				exact = decimal.Parse(value.ToString("R", CultureInfo.InvariantCulture),
				                      NumberStyles.Float, CultureInfo.InvariantCulture);
			}
			catch (OverflowException)
			{
				return SheetStatus.ErrOverflow;
			}
			catch (FormatException)
			{
				return SheetStatus.ErrFormat;
			}

			Span<int> bits = stackalloc int[4];
			decimal.GetBits(exact, bits);

			/* A decimal is a 96-bit mantissa; anything above 64 bits has no int64
			   numerator and is refused rather than truncated. */
			if (bits[2] != 0)
			{
				return SheetStatus.ErrOverflow;
			}

			ulong mantissa = ((ulong)(uint)bits[1] << 32) | (uint)bits[0];
			if (mantissa > long.MaxValue)
			{
				return SheetStatus.ErrOverflow;
			}

			int scale = (bits[3] >> 16) & 0xFF;
			bool negative = (bits[3] & unchecked((int)0x80000000)) != 0;

			long denominator = 1L;
			for (int i = 0; i < scale; ++i)
			{
				if (denominator > long.MaxValue / 10L)
				{
					return SheetStatus.ErrOverflow;
				}
				denominator *= 10L;
			}

			long numerator = (long)mantissa;
			long divisor = Gcd(numerator, denominator);

			if (divisor > 1L)
			{
				numerator /= divisor;
				denominator /= divisor;
			}

			rational.Numerator = negative ? -numerator : numerator;
			rational.Denominator = denominator;

			return SheetStatus.Ok;
		}

		/// <summary>
		/// Lowers a reference as written. A1 becomes a REF and A1:B3 a RANGE. The
		/// A1 grammar itself is parsed by khz_formula_parse_ref so both layers
		/// resolve a reference through exactly one implementation.
		/// </summary>
		private static SheetStatus LowerReference(byte* block, string text, ref IntPtr result)
		{
			if (string.IsNullOrEmpty(text))
			{
				return SheetStatus.ErrFormat;
			}

			/* A sheet qualifier names something this engine does not have yet. One
			   sheet per workbook, so Sheet2!A1 is refused instead of being read as
			   A1 on the only sheet there is. */
			if (text.IndexOf('!') >= 0)
			{
				return SheetStatus.ErrUnsupported;
			}

			int colon = text.IndexOf(':');
			IntPtr node = IntPtr.Zero;
			int status;

			if (colon < 0)
			{
				uint col, row;
				SheetStatus parsed = ParseReference(text, out col, out row);

				if (parsed != SheetStatus.Ok)
				{
					return parsed;
				}

				status = KhzNativeFormula.Ref(block, col, row, &node);
			}
			else
			{
				uint col0, row0, col1, row1;
				SheetStatus left = ParseReference(text.Substring(0, colon), out col0, out row0);
				SheetStatus right = ParseReference(text.Substring(colon + 1), out col1, out row1);

				if (left != SheetStatus.Ok)
				{
					return left;
				}
				if (right != SheetStatus.Ok)
				{
					return right;
				}

				status = KhzNativeFormula.Range(block, col0, row0, col1, row1, &node);
			}

			if (status == 0)
			{
				result = node;
			}

			return (SheetStatus)status;
		}

		private static SheetStatus ParseReference(string text, out uint col, out uint row)
		{
			uint c = 0u;
			uint r = 0u;
			int status;

			col = 0u;
			row = 0u;

			if (string.IsNullOrEmpty(text))
			{
				return SheetStatus.ErrFormat;
			}

			byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(text);

			fixed (byte* p = utf8)
			{
				status = KhzNativeFormula.ParseRef(p, (nuint)utf8.Length, &c, &r);
			}

			if (status == 0)
			{
				col = c;
				row = r;
			}

			return (SheetStatus)status;
		}

		private static SheetStatus LowerUnary(byte* block, UnaryNode unary, int depth, ref IntPtr result)
		{
			IntPtr operand = IntPtr.Zero;
			SheetStatus lowered = Lower(block, unary.Operand, depth + 1, ref operand);

			if (lowered != SheetStatus.Ok)
			{
				return lowered;
			}

			if (unary.Operator == "+")
			{
				/* Unary plus is identity. */
				result = operand;
				return SheetStatus.Ok;
			}
			if (unary.Operator != "-")
			{
				return SheetStatus.ErrUnsupported;
			}

			return Combine(block, KhzFormulaOp.Negate, operand, IntPtr.Zero, ref result);
		}

		/// <summary>
		/// A trailing percent is a division by one hundred, lowered as such. There
		/// is no PERCENT operation in the IR because there does not need to be: 5%
		/// is exactly 1/20 and the rational arithmetic keeps it that way.
		/// </summary>
		private static SheetStatus LowerPostfix(byte* block, PostfixNode postfix, int depth,
		                                        ref IntPtr result)
		{
			if (postfix.Operator != "%")
			{
				return SheetStatus.ErrUnsupported;
			}

			IntPtr operand = IntPtr.Zero;
			SheetStatus lowered = Lower(block, postfix.Operand, depth + 1, ref operand);

			if (lowered != SheetStatus.Ok)
			{
				return lowered;
			}

			KhzRational hundred;
			hundred.Numerator = 100L;
			hundred.Denominator = 1L;

			IntPtr divisor = IntPtr.Zero;
			int status = KhzNativeFormula.Const(block, hundred, &divisor);

			if (status != 0)
			{
				return (SheetStatus)status;
			}

			return Combine(block, KhzFormulaOp.Divide, operand, divisor, ref result);
		}

		private static SheetStatus LowerBinary(byte* block, BinaryNode binary, int depth,
		                                       ref IntPtr result)
		{
			KhzFormulaOp op;

			switch (binary.Operator)
			{
				case "+": op = KhzFormulaOp.Add; break;
				case "-": op = KhzFormulaOp.Subtract; break;
				case "*": op = KhzFormulaOp.Multiply; break;
				case "/": op = KhzFormulaOp.Divide; break;
				default:
					/* Exponent, concatenation, the comparisons, union and
					   intersection are all real Excel operators with no IR
					   operation behind them yet. Refused, not faked. */
					return SheetStatus.ErrUnsupported;
			}

			IntPtr left = IntPtr.Zero;
			SheetStatus lowered = Lower(block, binary.Left, depth + 1, ref left);

			if (lowered != SheetStatus.Ok)
			{
				return lowered;
			}

			IntPtr right = IntPtr.Zero;
			lowered = Lower(block, binary.Right, depth + 1, ref right);

			if (lowered != SheetStatus.Ok)
			{
				return lowered;
			}

			return Combine(block, op, left, right, ref result);
		}

		private static SheetStatus LowerFunction(byte* block, FunctionNode function, int depth,
		                                         ref IntPtr result)
		{
			KhzFormulaOp op;
			string name = function.Name == null
				? string.Empty
				: function.Name.ToUpperInvariant();

			switch (name)
			{
				case "SUM": op = KhzFormulaOp.Sum; break;
				case "AVG":
				case "AVERAGE": op = KhzFormulaOp.Average; break;
				case "MIN": op = KhzFormulaOp.Min; break;
				case "MAX": op = KhzFormulaOp.Max; break;
				default:
					/* Four functions exist. Every other name is refused here and
					   would be #NAME? in a cell. */
					return SheetStatus.ErrUnsupported;
			}

			/* Bound to a local, then checked once. The previous form tested
			   function.Arguments for null inside a conditional and indexed it two
			   statements later, which is provable to a reader but not to flow
			   analysis - hence CS8602. Suppressing that with ! would have been
			   the wrong answer: nothing in the method's own text guaranteed the
			   property returned the same non-null value on the second read. */
			IReadOnlyList<FormulaNode>? arguments = function.Arguments;

			if (arguments == null)
			{
				return SheetStatus.ErrFormat;
			}

			int count = arguments.Count;
			if (count == 0 || count > 64)
			{
				return SheetStatus.ErrFormat;
			}

			IntPtr* children = stackalloc IntPtr[count];

			for (int i = 0; i < count; ++i)
			{
				IntPtr child = IntPtr.Zero;
				SheetStatus lowered = Lower(block, arguments[i], depth + 1, ref child);

				if (lowered != SheetStatus.Ok)
				{
					return lowered;
				}

				children[i] = child;
			}

			IntPtr node = IntPtr.Zero;
			int status = KhzNativeFormula.Node(block, (int)op, children, (nuint)count, &node);

			if (status == 0)
			{
				result = node;
			}

			return (SheetStatus)status;
		}

		private static SheetStatus Combine(byte* block, KhzFormulaOp op, IntPtr left, IntPtr right,
		                                   ref IntPtr result)
		{
			IntPtr* children = stackalloc IntPtr[2];
			nuint count;

			children[0] = left;

			if (right == IntPtr.Zero)
			{
				count = (nuint)1;
			}
			else
			{
				children[1] = right;
				count = (nuint)2;
			}

			IntPtr node = IntPtr.Zero;
			int status = KhzNativeFormula.Node(block, (int)op, children, count, &node);

			if (status == 0)
			{
				result = node;
			}

			return (SheetStatus)status;
		}

		private static FormulaParseFailure ReadFailure(ref KhzFormulaParseErrorNative error)
		{
			string expected = string.Empty;

			fixed (byte* p = error.Expected)
			{
				int length = 0;

				while (length < 32 && p[length] != 0)
				{
					++length;
				}

				if (length > 0)
				{
					expected = System.Text.Encoding.UTF8.GetString(p, length);
				}
			}

			return new FormulaParseFailure((ulong)error.Offset, expected);
		}
	}
}
