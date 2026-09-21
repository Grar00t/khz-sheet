using System;
using KHZ.Sheet.Core;

namespace KHZ.Sheet.Tests
{
	internal static class Program
	{
		private static int failures;
		private static int cases;

		private static void Check(NativeSheet sheet, string expression, long num, long den)
		{
			cases++;

			SheetStatus parsed = FormulaParser.TryParse(expression, out FormulaNode? node);
			if (parsed != SheetStatus.Ok || node == null)
			{
				failures++;
				Console.WriteLine("  " + expression + " -> parse " + parsed + "  FAIL");
				return;
			}

			SheetStatus status = sheet.TryEvaluate(0u, 0u, node, out KhzFormulaResult result);
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

		private static void CheckCellError(NativeSheet sheet, string expression, CellErrorCode expected)
		{
			cases++;

			SheetStatus parsed = FormulaParser.TryParse(expression, out FormulaNode? node);
			if (parsed != SheetStatus.Ok || node == null)
			{
				failures++;
				Console.WriteLine("  " + expression + " -> parse " + parsed + "  FAIL");
				return;
			}

			SheetStatus status = sheet.TryEvaluate(0u, 0u, node, out KhzFormulaResult result);
			bool ok = status == SheetStatus.Ok
				&& result.IsError
				&& result.Error == (uint)expected;

			if (!ok)
			{
				failures++;
			}

			Console.WriteLine("  " + expression + " -> status=" + status
				+ " error=" + result.Error
				+ (ok ? "  OK" : "  FAIL, expected " + expected));
		}

		private static void Refuse(NativeSheet sheet, string expression, SheetStatus expected)
		{
			cases++;

			SheetStatus parsed = FormulaParser.TryParse(expression, out FormulaNode? node);
			if (parsed != SheetStatus.Ok || node == null)
			{
				failures++;
				Console.WriteLine("  " + expression + " -> parse " + parsed
					+ "  FAIL, expected lowering to return " + expected);
				return;
			}

			SheetStatus status = sheet.TryEvaluate(0u, 0u, node, out KhzFormulaResult result);
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
			SheetStatus abi = KhzAbi.Verify(out string detail);
			Console.WriteLine("abi      = " + abi + " (" + detail + ")");

			if (abi != SheetStatus.Ok)
			{
				Console.WriteLine("FAIL abi gate");
				return 1;
			}

			SheetStatus created = NativeSheet.TryCreate((nuint)(1 << 22), (nuint)4096, out NativeSheet? sheet);
			Console.WriteLine("create   = " + created);

			if (created != SheetStatus.Ok || sheet == null)
			{
				Console.WriteLine("FAIL sheet");
				return 1;
			}

			using (sheet)
			{
				Console.WriteLine("exact literals:");
				Check(sheet, "0.1", 1L, 10L);
				Check(sheet, "0.2", 1L, 5L);
				Check(sheet, "0.07", 7L, 100L);
				Check(sheet, "1.5", 3L, 2L);
				Check(sheet, "0.10", 1L, 10L);
				Check(sheet, "50.00", 50L, 1L);
				Check(sheet, "0", 0L, 1L);
				Check(sheet, "7", 7L, 1L);
				Check(sheet, "1e3", 1000L, 1L);
				Check(sheet, "2.5E-4", 1L, 4000L);

				Console.WriteLine("arithmetic:");
				Check(sheet, "0.1+0.2", 3L, 10L);
				Check(sheet, "1/3", 1L, 3L);
				Check(sheet, "50%", 1L, 2L);
				Check(sheet, "0.5%", 1L, 200L);
				Check(sheet, "-0.25", -1L, 4L);

				Console.WriteLine("power:");
				Check(sheet, "2^3", 8L, 1L);
				Check(sheet, "2^-3", 1L, 8L);
				Check(sheet, "(-2)^3", -8L, 1L);
				Check(sheet, "0^0", 1L, 1L);
				CheckCellError(sheet, "2^0.5", CellErrorCode.Num);

				Console.WriteLine("refusals:");
				Refuse(sheet, "1e-23", SheetStatus.ErrOverflow);
				Refuse(sheet, "9223372036854775808", SheetStatus.ErrOverflow);
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
