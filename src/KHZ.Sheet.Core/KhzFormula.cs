using System;
using System.Globalization;
using System.Runtime.InteropServices;

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
		/// KhzFormula is treated as an opaque block rather than mirrored field by
		/// field. Its real size is seven words; 256 bytes is a deliberate
		/// over-allocation so a field added on the C side cannot silently
		/// corrupt the stack here. KhzAbiSizes does not yet publish
		/// sizeof(KhzFormula) - adding khz_abi_formula_bytes() and asserting
		/// against it is Phase 94 work, and until then this number is the one
		/// unverified layout assumption in this file.
		/// </summary>
		internal const int FormulaBlockBytes = 256;

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

		/// <summary>Recalculates every formula cell in topological order.</summary>
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

			byte* block = stackalloc byte[KhzNativeFormula.FormulaBlockBytes];
			new Span<byte>(block, KhzNativeFormula.FormulaBlockBytes).Clear();

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

			byte* block = stackalloc byte[KhzNativeFormula.FormulaBlockBytes];
			new Span<byte>(block, KhzNativeFormula.FormulaBlockBytes).Clear();

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
					return LowerNumber(block, number.Value, ref result);

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
		/// Converts a managed double literal to an exact rational.
		///
		/// The managed lexer stores literals as double, so 0.1 has already been
		/// rounded before this method sees it. Casting the binary value directly
		/// would yield 3602879701896397/36028797018963968 - exact, but not the
		/// number the user typed. Going through decimal recovers the shortest
		/// round-trippable decimal instead, so 0.1 becomes 1/10.
		///
		/// This is a repair, not a design. The real fix is for FormulaLexer to
		/// keep the literal text and hand it to the C parser, which already
		/// converts digits to a rational without a float in the path. Recorded as
		/// Phase 94 work; until then, prefer SetFormula over lowering a tree.
		/// </summary>
		private static SheetStatus LowerNumber(byte* block, double value, ref IntPtr result)
		{
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

			KhzRational rational;
			rational.Numerator = negative ? -(long)mantissa : (long)mantissa;
			rational.Denominator = denominator;

			IntPtr node = IntPtr.Zero;
			int status = KhzNativeFormula.Const(block, rational, &node);

			if (status == 0)
			{
				result = node;
			}

			return (SheetStatus)status;
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

			int count = function.Arguments == null ? 0 : function.Arguments.Count;
			if (count == 0 || count > 64)
			{
				return SheetStatus.ErrFormat;
			}

			IntPtr* children = stackalloc IntPtr[count];

			for (int i = 0; i < count; ++i)
			{
				IntPtr child = IntPtr.Zero;
				SheetStatus lowered = Lower(block, function.Arguments[i], depth + 1, ref child);

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
