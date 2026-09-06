using System;
using KHZ.Sheet.Core;

namespace KHZ.Sheet.Tests
{
	/// <summary>
	/// Installs a formula in a cell through the C parser, recalculates, reads
	/// the result back, invalidates an input, and recalculates again.
	///
	/// This is the path KhzFormula.cs calls preferred: the formula text goes
	/// straight to the C parser, so no managed tree is built and no literal
	/// passes through a double. Until this test it had never run from managed
	/// code, because the sheet pointer every entry point needs was internal
	/// until Phase 98 Push J.
	///
	/// It reaches what a literal test cannot: the C formula parser, the
	/// dependency edges that parser declares, dirty marking, topological
	/// recalculation, and the proof chain over a computed rather than assigned
	/// value.
	///
	/// Note on types: a cell exposes its value as KhzRationalNative, whose
	/// fields are Num and Den, while a formula result exposes KhzRational,
	/// whose fields are Numerator and Denominator. Same C struct, two managed
	/// mirrors, different member names. That inconsistency cost this file one
	/// failed build.
	///
	/// Exits non-zero on failure.
	/// </summary>
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

		/// <summary>
		/// Installs the formula, trying it with and without a leading '='.
		///
		/// Which form the C parser wants is not documented anywhere I can read
		/// from here, so it is discovered rather than assumed. Asserting the
		/// wrong one would produce a red test that says nothing about the engine.
		/// Both forms failing is a real failure and is reported with the offset
		/// and expectation the parser gave.
		/// </summary>
		private static SheetStatus Install(NativeSheet sheet, uint col, uint row, string body,
		                                   out string accepted)
		{
			FormulaParseFailure failure;
			string withEquals = "=" + body;

			SheetStatus status = sheet.TrySetFormula(col, row, withEquals, out failure);

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
			string detail;
			SheetStatus abi = KhzAbi.Verify(out detail);

			Console.WriteLine("abi      = " + abi + " (" + detail + ")");

			if (abi != SheetStatus.Ok)
			{
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
				/* A1 and A2 are col 0, rows 0 and 1. */
				Console.WriteLine("inputs:");
				Expect(sheet.SetInt64(0u, 0u, 10L) == SheetStatus.Ok, "A1 = 10");
				Expect(sheet.SetInt64(0u, 1u, 20L) == SheetStatus.Ok, "A2 = 20");

				Console.WriteLine("install:");

				string accepted;
				SheetStatus installed = Install(sheet, 1u, 0u, "SUM(A1:A2)", out accepted);

				if (installed != SheetStatus.Ok)
				{
					Fail("B1 formula refused in both forms: " + installed);
					Console.WriteLine("FAIL failures=" + failures);
					return 1;
				}

				Console.WriteLine("  OK   B1 accepted as " + accepted);

				Console.WriteLine("recalc:");

				ulong evaluated;
				SheetStatus recalculated = sheet.TryRecalculate(out evaluated);

				Expect(recalculated == SheetStatus.Ok, "recalculate -> " + recalculated);

				/* Only that something was evaluated. The exact count depends on
				   whether recalc visits inputs as well as formulas, which is not a
				   contract this test should invent. */
				Expect(evaluated >= 1UL, "evaluated = " + evaluated);

				KhzCellNative cell;
				SheetStatus read = sheet.TryGetCell(1u, 0u, out cell);

				if (read != SheetStatus.Ok)
				{
					Fail("read B1 -> " + read);
					Console.WriteLine("FAIL failures=" + failures);
					return 1;
				}

				/* Printed, not asserted: the cell may keep Kind Formula with its
				   value populated, or become Rational. Both are defensible and I
				   do not know which the C layer chose. */
				Console.WriteLine("  note B1 kind = " + cell.Kind
					+ ", dirty = " + cell.IsDirty
					+ ", revision = " + cell.Revision);

				Expect(cell.Value.Num == 30L && cell.Value.Den == 1L,
					"B1 = " + cell.Value + ", expected 30/1");

				Expect(cell.IsCommitted, "B1 committed onto the chain");

				Console.WriteLine("invalidate:");

				/* Changing an input must make the dependent stale. This is the
				   dependency edge the C parser declared when the formula was
				   installed - nothing in managed code told it A1 feeds B1. */
				Expect(sheet.SetInt64(0u, 0u, 15L) == SheetStatus.Ok, "A1 = 15");

				KhzCellNative stale;
				if (sheet.TryGetCell(1u, 0u, out stale) == SheetStatus.Ok)
				{
					Console.WriteLine("  note B1 dirty after A1 changed = " + stale.IsDirty);
				}

				ulong second;
				SheetStatus again = sheet.TryRecalculate(out second);

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

				Console.WriteLine("chain:");

				nuint failedIndex;
				SheetStatus verified = sheet.VerifyChain(out failedIndex);

				Expect(verified == SheetStatus.Ok,
					"verify chain -> " + verified + ", failed index " + (ulong)failedIndex);

				string proof;
				if (sheet.TryProofHex(out proof) == SheetStatus.Ok)
				{
					Expect(proof.Length == 64, "proof = " + proof);
				}
				else
				{
					Fail("proof hex");
				}

				Console.WriteLine("  note cells = " + (ulong)sheet.CellCount
					+ ", commits = " + sheet.Commits);

				Console.WriteLine("arena:");

				nuint used, capacity, peak;
				ulong allocations, rejections;

				if (sheet.TryArenaStats(out used, out capacity, out peak,
				                        out allocations, out rejections) == SheetStatus.Ok)
				{
					Console.WriteLine("  note used = " + (ulong)used
						+ ", capacity = " + (ulong)capacity
						+ ", peak = " + (ulong)peak
						+ ", allocations = " + allocations);

					/* Zero rejections is the Ring-0 claim: nothing was refused and
					   nothing fell back to the heap. */
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
