using System;
using KHZ.Sheet.Core;

namespace KHZ.Sheet.Tests
{
	/// <summary>
	/// Checks that a numeric literal reaches the C evaluator as the exact
	/// rational the text denotes.
	///
	/// This is the end-to-end proof of the Phase 98 exact-literal path:
	/// FormulaLexer captures the literal text, FormulaParser carries it into
	/// NumberNode.RawText, KhzFormula converts the digits straight to num/den
	/// with no double in the path, and the C rational arithmetic keeps the
	/// result exact. Every stage is exercised; none is mocked.
	///
	/// The two refusals matter as much as the values. A literal with no exact
	/// int64 rational, and an operator with no IR operation, must both fail. A
	/// change that makes either succeed by approximating would be a regression
	/// even though it would look like added capability.
	///
	/// Exits non-zero if any case fails, so it can be wired into ctest.
	/// </summary>
	internal static class Program
	{
		private static int failures;
		private static int cases;

		/// <summary>
		/// Parses one expression, evaluates it, and compares num/den exactly.
		/// The comparison is on both fields, not on a ratio: 2/20 and 1/10 are
		/// the same number but only one of them is reduced, and the reduction is
		/// part of what is being tested.
		/// </summary>
		private static void Check(NativeSheet sheet, string expression, long num, long den)
		{
			cases++;

			FormulaNode? node;
			SheetStatus parsed = FormulaParser.TryParse(expression, out node);

			if (parsed != SheetStatus.Ok || node == null)
			{
				failures++;
				Console.WriteLine("  " + expression + " -> parse " + parsed + "  FAIL");
				return;
			}

			KhzFormulaResult result;
			SheetStatus status = sheet.TryEvaluate(0u, 0u, node, out result);

			if (status != SheetStatus.Ok)
			{
				failures++;
				Console.WriteLine("  " + expression + " -> " + status
					+ "  FAIL, expected " + num + "/" + den);
				return;
			}

			if (result.IsError)
			{
				failures++;
				Console.WriteLine("  " + expression + " -> cell error " + result.Error
					+ "  FAIL, expected " + num + "/" + den);
				return;
			}

			bool ok = result.Value.Numerator == num && result.Value.Denominator == den;

			if (!ok)
			{
				failures++;
			}

			Console.WriteLine("  " + expression + " -> "
				+ result.Value.Numerator + "/" + result.Value.Denominator
				+ (ok ? "  OK" : "  FAIL, expected " + num + "/" + den));
		}

		/// <summary>
		/// Confirms an expression is refused, and refused with the expected
		/// status. A refusal for the wrong reason is not a pass: ErrFormat where
		/// ErrOverflow belongs would mean the value was rejected by the grammar
		/// rather than by the range check that is being tested.
		/// </summary>
		private static void Refuse(NativeSheet sheet, string expression, SheetStatus expected)
		{
			cases++;

			FormulaNode? node;
			SheetStatus parsed = FormulaParser.TryParse(expression, out node);

			if (parsed != SheetStatus.Ok || node == null)
			{
				/* Refused by the lexer or parser instead of the lowerer. Reported
				   rather than counted as a pass, because it means the case never
				   reached the code it was written to test. */
				failures++;
				Console.WriteLine("  " + expression + " -> parse " + parsed
					+ "  FAIL, expected lowering to return " + expected);
				return;
			}

			KhzFormulaResult result;
			SheetStatus status = sheet.TryEvaluate(0u, 0u, node, out result);

			bool ok = status == expected;

			if (!ok)
			{
				failures++;
			}

			Console.WriteLine("  " + expression + " -> " + status
				+ (ok ? "  OK refused" : "  FAIL, expected " + expected));
		}

		private static int Main()
		{
			string detail;
			SheetStatus abi = KhzAbi.Verify(out detail);

			Console.WriteLine("abi      = " + abi + " (" + detail + ")");

			if (abi != SheetStatus.Ok)
			{
				/* Refusing here rather than running the cases anyway. Against a
				   library whose struct layout disagrees with these bindings, every
				   result below would be meaningless. */
				Console.WriteLine("FAIL abi gate");
				return 1;
			}

			NativeSheet? sheet;
			SheetStatus created = NativeSheet.TryCreate((nuint)(1 << 22), (nuint)4096, out sheet);

			Console.WriteLine("create   = " + created);

			if (created != SheetStatus.Ok || sheet == null)
			{
				Console.WriteLine("FAIL sheet");
				return 1;
			}

			using (sheet)
			{
				Console.WriteLine("exact literals:");

				/* The literals a binary float cannot hold. These are the reason
				   the exact path exists. */
				Check(sheet, "0.1", 1L, 10L);
				Check(sheet, "0.2", 1L, 5L);
				Check(sheet, "0.07", 7L, 100L);
				Check(sheet, "1.5", 3L, 2L);

				/* Trailing zeros, which the gcd reduction makes irrelevant.
				   Without it these would be 10/100 and 5000/100. */
				Check(sheet, "0.10", 1L, 10L);
				Check(sheet, "50.00", 50L, 1L);

				/* Integers, including zero, which must reduce to 0/1 rather than
				   keeping whatever denominator the scale produced. */
				Check(sheet, "0", 0L, 1L);
				Check(sheet, "7", 7L, 1L);

				/* Scientific notation, handled exactly rather than refused:
				   mantissa and exponent are both exact rationals. */
				Check(sheet, "1e3", 1000L, 1L);
				Check(sheet, "2.5E-4", 1L, 4000L);

				Console.WriteLine("arithmetic:");

				/* The canonical floating point failure. In double arithmetic this
				   is 0.30000000000000004; here it is 3/10. */
				Check(sheet, "0.1+0.2", 3L, 10L);

				/* Division stays rational instead of becoming 0.333... */
				Check(sheet, "1/3", 1L, 3L);

				/* Percent is a divide by one hundred, so it composes with the
				   exact literals rather than being a special case. */
				Check(sheet, "50%", 1L, 2L);
				Check(sheet, "0.5%", 1L, 200L);

				/* Unary minus lowers to Negate over an exact operand. */
				Check(sheet, "-0.25", -1L, 4L);

				Console.WriteLine("refusals:");

				/* No exact int64 rational: the denominator would be 1e23.
				   Refused, not rounded. */
				Refuse(sheet, "1e-23", SheetStatus.ErrOverflow);

				/* One past int64 range. The mantissa check must catch this
				   rather than wrapping to a negative numerator. */
				Refuse(sheet, "9223372036854775808", SheetStatus.ErrOverflow);

				/* Real Excel operators with no IR operation yet. Refused rather
				   than approximated. */
				Refuse(sheet, "2^3", SheetStatus.ErrUnsupported);
				Refuse(sheet, "1&2", SheetStatus.ErrUnsupported);

				Console.WriteLine("cases    = " + cases);
				Console.WriteLine(failures == 0
					? "ALL PASS failures=0"
					: "FAIL failures=" + failures);
			}

			return failures == 0 ? 0 : 1;
		}
	}
}
