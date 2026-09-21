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
		Power = 12,
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
				return SheetStatus.ErrState;
			}
			catch (EntryPointNotFoundException)
			{
				return SheetStatus.ErrUnsupported;
			}

			if (reported == 0UL)
			{
				return SheetStatus.ErrState;
			}
			if (reported > (ulong)StackBlockBytes)
			{
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
	/// Lowers the managed syntax tree onto the C formula IR. C owns evaluation;
	/// this layer translates shapes only and never computes an answer itself.
	/// </summary>
	public static unsafe class KhzFormula
	{
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

			return (SheetStatus)status;
		}

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
			KhzNativeFormula.Abandon(block);

			if (status == 0)
			{
				declared = count;
			}

			return (SheetStatus)status;
		}

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
					return LowerNumber(block, number, ref result);
				case GroupNode group:
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
					return SheetStatus.ErrUnsupported;
			}
		}

		private static SheetStatus LowerNumber(byte* block, NumberNode number, ref IntPtr result)
		{
			if (number == null)
			{
				return SheetStatus.ErrNull;
			}

			KhzRational rational;
			string? raw = number.RawText;

			if (raw != null)
			{
				SheetStatus exact = TryExactRational(raw, out rational);
				if (exact != SheetStatus.Ok)
				{
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

		private static SheetStatus LowerReference(byte* block, string text, ref IntPtr result)
		{
			if (string.IsNullOrEmpty(text))
			{
				return SheetStatus.ErrFormat;
			}
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
				result = operand;
				return SheetStatus.Ok;
			}
			if (unary.Operator != "-")
			{
				return SheetStatus.ErrUnsupported;
			}
			return Combine(block, KhzFormulaOp.Negate, operand, IntPtr.Zero, ref result);
		}

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
				case "^": op = KhzFormulaOp.Power; break;
				default:
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
					return SheetStatus.ErrUnsupported;
			}

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
