using System;
using KHZ.Sheet.Core;

namespace KHZ.Sheet.Tests
{
	internal static class Program
	{
		private static int failures;

		private static void Fail(string message)
		{
			failures++;
			Console.WriteLine("  FAIL " + message);
		}

		private static void Expect(bool condition, string message)
		{
			if (condition)
			{
				Console.WriteLine("  OK   " + message);
			}
			else
			{
				Fail(message);
			}
		}

		private static SheetStatus Install(NativeSheet sheet, uint col, uint row, string body,
		                                   out string accepted)
		{
			string withEquals = "=" + body;
			SheetStatus status = sheet.TrySetFormula(col, row, withEquals, out FormulaParseFailure failure);

			if (status == SheetStatus.Ok)
			{
				accepted = withEquals;
				return status;
			}

			Console.WriteLine("  note " + withEquals + " -> " + status
				+ " at offset " + failure.Offset
				+ (failure.Expected.Length == 0 ? string.Empty : ", expected " + failure.Expected));

			SheetStatus bare = sheet.TrySetFormula(col, row, body, out failure);
			if (bare == SheetStatus.Ok)
			{
				accepted = body;
				return bare;
			}

			Console.WriteLine("  note " + body + " -> " + bare
				+ " at offset " + failure.Offset
				+ (failure.Expected.Length == 0 ? string.Empty : ", expected " + failure.Expected));

			accepted = string.Empty;
			return bare;
		}

		private static unsafe int Main()
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
				Console.WriteLine("dependency formula:");
				Expect(sheet.SetInt64(0u, 0u, 10L) == SheetStatus.Ok, "A1 = 10");
				Expect(sheet.SetInt64(0u, 1u, 20L) == SheetStatus.Ok, "A2 = 20");

				SheetStatus installed = Install(sheet, 1u, 0u, "SUM(A1:A2)", out string accepted);
				Expect(installed == SheetStatus.Ok, "B1 accepted as " + accepted);
				if (installed != SheetStatus.Ok)
				{
					return 1;
				}

				SheetStatus recalculated = sheet.TryRecalculate(out ulong evaluated);
				Expect(recalculated == SheetStatus.Ok, "recalculate -> " + recalculated);
				Expect(evaluated >= 1UL, "evaluated = " + evaluated);

				SheetStatus read = sheet.TryGetCell(1u, 0u, out KhzCellNative cell);
				Expect(read == SheetStatus.Ok, "read B1 -> " + read);
				if (read != SheetStatus.Ok)
				{
					return 1;
				}

				Expect(cell.Value.Num == 30L && cell.Value.Den == 1L,
					"B1 = " + cell.Value + ", expected 30/1");
				Expect(cell.IsCommitted, "B1 committed onto the chain");

				Expect(sheet.SetInt64(0u, 0u, 15L) == SheetStatus.Ok, "A1 = 15");
				if (sheet.TryGetCell(1u, 0u, out KhzCellNative stale) == SheetStatus.Ok)
				{
					Expect(stale.IsDirty, "B1 dirty after A1 changed");
				}

				SheetStatus again = sheet.TryRecalculate(out ulong second);
				Expect(again == SheetStatus.Ok, "recalculate -> " + again);
				Console.WriteLine("  note evaluated = " + second);
				if (sheet.TryGetCell(1u, 0u, out cell) == SheetStatus.Ok)
				{
					Expect(cell.Value.Num == 35L && cell.Value.Den == 1L,
						"B1 = " + cell.Value + " after A1 = 15, expected 35/1");
				}
				else
				{
					Fail("re-read B1");
				}

				Console.WriteLine("native source power parser:");
				SheetStatus p1 = Install(sheet, 2u, 0u, "2^3", out string powerAccepted);
				SheetStatus p2 = Install(sheet, 3u, 0u, "2^-3", out string negativeAccepted);
				SheetStatus p3 = Install(sheet, 4u, 0u, "2^0.5", out string fractionalAccepted);
				Expect(p1 == SheetStatus.Ok, "C1 accepted as " + powerAccepted);
				Expect(p2 == SheetStatus.Ok, "D1 accepted as " + negativeAccepted);
				Expect(p3 == SheetStatus.Ok, "E1 accepted as " + fractionalAccepted);

				SheetStatus powerRecalc = sheet.TryRecalculate(out ulong powerEvaluated);
				Expect(powerRecalc == SheetStatus.Ok, "power recalculate -> " + powerRecalc);
				Expect(powerEvaluated >= 3UL, "power evaluated = " + powerEvaluated);

				if (sheet.TryGetCell(2u, 0u, out KhzCellNative c1) == SheetStatus.Ok)
				{
					Expect(c1.Value.Num == 8L && c1.Value.Den == 1L, "C1 = 8/1");
				}
				else
				{
					Fail("read C1");
				}

				if (sheet.TryGetCell(3u, 0u, out KhzCellNative d1) == SheetStatus.Ok)
				{
					Expect(d1.Value.Num == 1L && d1.Value.Den == 8L, "D1 = 1/8");
				}
				else
				{
					Fail("read D1");
				}

				if (sheet.TryGetCell(4u, 0u, out KhzCellNative e1) == SheetStatus.Ok)
				{
					Expect(e1.ErrorCode == CellErrorCode.Num, "E1 = #NUM! for fractional exponent");
				}
				else
				{
					Fail("read E1");
				}

				Console.WriteLine("chain:");
				SheetStatus verified = sheet.VerifyChain(out nuint failedIndex);
				Expect(verified == SheetStatus.Ok,
					"verify chain -> " + verified + ", failed index " + (ulong)failedIndex);

				if (sheet.TryProofHex(out string proof) == SheetStatus.Ok)
				{
					Expect(proof.Length == 64, "proof = " + proof);
				}
				else
				{
					Fail("proof hex");
				}

				Console.WriteLine("arena:");
				if (sheet.TryArenaStats(out nuint used, out nuint capacity, out nuint peak,
				                        out ulong allocations, out ulong rejections) == SheetStatus.Ok)
				{
					Console.WriteLine("  note used = " + (ulong)used
						+ ", capacity = " + (ulong)capacity
						+ ", peak = " + (ulong)peak
						+ ", allocations = " + allocations);
					Expect(rejections == 0UL, "arena rejections = " + rejections);
				}
				else
				{
					Fail("arena stats");
				}

				Console.WriteLine(failures == 0
					? "ALL PASS failures=0"
					: "FAIL failures=" + failures);
			}

			return failures == 0 ? 0 : 1;
		}
	}
}
