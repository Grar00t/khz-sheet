using System;
using KHZ.Sheet.Core;

namespace KHZ.Sheet.Tests
{
    internal static class Program
    {
        private static int failures;

        private static void Expect(bool condition, string message)
        {
            Console.WriteLine((condition ? "  OK   " : "  FAIL ") + message);
            if (!condition)
            {
                failures++;
            }
        }

        private static SheetStatus Install(NativeSheet sheet, uint col, uint row, string body)
        {
            return sheet.TrySetFormula(col, row, "=" + body, out FormulaParseFailure _);
        }

        private static bool IsValue(NativeSheet sheet, uint col, uint row, long num, long den)
        {
            if (sheet.TryGetCell(col, row, out KhzCellNative cell) != SheetStatus.Ok)
            {
                return false;
            }

            return cell.ErrorCode == CellErrorCode.None
                && cell.Value.Num == num
                && cell.Value.Den == den;
        }

        private static NativeSheet? CreateSheet()
        {
            SheetStatus status = NativeSheet.TryCreate((nuint)(1 << 20), (nuint)128,
                                                       out NativeSheet? sheet);
            Expect(status == SheetStatus.Ok && sheet != null, "create isolated sheet -> " + status);
            return sheet;
        }

        private static void FormulaToFormula()
        {
            NativeSheet? sheet = CreateSheet();
            if (sheet == null)
            {
                return;
            }

            using (sheet)
            {
                Console.WriteLine("formula -> formula rewrite:");
                Expect(sheet.SetInt64(0u, 0u, 10L) == SheetStatus.Ok, "A1 = 10");
                Expect(Install(sheet, 1u, 0u, "A1") == SheetStatus.Ok, "B1 = A1");
                Expect(sheet.TryRecalculate(out ulong first) == SheetStatus.Ok, "first recalc");
                Expect(first >= 1UL, "first recalc evaluated formula");
                Expect(IsValue(sheet, 1u, 0u, 10L, 1L), "B1 = 10/1");

                Expect(Install(sheet, 1u, 0u, "1") == SheetStatus.Ok, "B1 = 1");
                Expect(sheet.TryRecalculate(out ulong second) == SheetStatus.Ok, "second recalc");
                Expect(second >= 1UL, "second recalc evaluated rewritten formula");
                Expect(IsValue(sheet, 1u, 0u, 1L, 1L), "B1 = 1/1");

                Expect(Install(sheet, 0u, 0u, "B1") == SheetStatus.Ok, "A1 = B1");
                SheetStatus thirdStatus = sheet.TryRecalculate(out ulong third);
                Expect(thirdStatus == SheetStatus.Ok,
                       "no stale formula edge cycle -> " + thirdStatus);
                if (thirdStatus == SheetStatus.Ok)
                {
                    Expect(third >= 1UL, "third recalc evaluated formula");
                    Expect(IsValue(sheet, 0u, 0u, 1L, 1L), "A1 = 1/1");
                }

                Expect(sheet.VerifyChain(out nuint failedIndex) == SheetStatus.Ok,
                       "formula rewrite proof verifies at index " + (ulong)failedIndex);
            }
        }

        private static void FormulaToValue()
        {
            NativeSheet? sheet = CreateSheet();
            if (sheet == null)
            {
                return;
            }

            using (sheet)
            {
                Console.WriteLine("formula -> value rewrite:");
                Expect(sheet.SetInt64(0u, 0u, 10L) == SheetStatus.Ok, "A1 = 10");
                Expect(Install(sheet, 1u, 0u, "A1") == SheetStatus.Ok, "B1 = A1");
                Expect(sheet.TryRecalculate(out ulong first) == SheetStatus.Ok, "initial recalc");
                Expect(first >= 1UL, "initial recalc evaluated formula");
                Expect(IsValue(sheet, 1u, 0u, 10L, 1L), "B1 = 10/1");

                Expect(sheet.SetInt64(1u, 0u, 1L) == SheetStatus.Ok,
                       "replace B1 formula with numeric 1");
                Expect(IsValue(sheet, 1u, 0u, 1L, 1L), "B1 value = 1/1");

                Expect(Install(sheet, 0u, 0u, "B1") == SheetStatus.Ok, "A1 = B1");
                SheetStatus recalc = sheet.TryRecalculate(out ulong evaluated);
                Expect(recalc == SheetStatus.Ok,
                       "no stale edge after formula -> value -> " + recalc);
                if (recalc == SheetStatus.Ok)
                {
                    Expect(evaluated >= 1UL, "reverse formula evaluated");
                    Expect(IsValue(sheet, 0u, 0u, 1L, 1L), "A1 = 1/1");
                }

                Expect(sheet.VerifyChain(out nuint failedIndex) == SheetStatus.Ok,
                       "formula-to-value proof verifies at index " + (ulong)failedIndex);
            }
        }

        private static void DirtyFlagDoesNotInvalidateProof()
        {
            NativeSheet? sheet = CreateSheet();
            if (sheet == null)
            {
                return;
            }

            using (sheet)
            {
                Console.WriteLine("dirty cache state vs proof:");
                Expect(sheet.SetInt64(0u, 0u, 10L) == SheetStatus.Ok, "A1 = 10");
                Expect(Install(sheet, 1u, 0u, "A1") == SheetStatus.Ok, "B1 = A1");
                Expect(sheet.TryRecalculate(out ulong first) == SheetStatus.Ok, "initial recalc");
                Expect(first >= 1UL, "initial formula evaluated");
                Expect(sheet.VerifyChain(out nuint before) == SheetStatus.Ok,
                       "proof valid while clean at index " + (ulong)before);

                Expect(sheet.SetInt64(0u, 0u, 11L) == SheetStatus.Ok,
                       "A1 = 11 marks B1 dirty");
                Expect(sheet.TryGetCell(1u, 0u, out KhzCellNative dirty) == SheetStatus.Ok
                       && dirty.IsDirty,
                       "B1 is dirty before recalculation");

                SheetStatus pendingProof = sheet.VerifyChain(out nuint pendingIndex);
                Expect(pendingProof == SheetStatus.Ok,
                       "proof remains valid while B1 is dirty -> " + pendingProof
                       + " at index " + (ulong)pendingIndex);

                Expect(sheet.TryRecalculate(out ulong second) == SheetStatus.Ok,
                       "recalculate dirty B1");
                Expect(second >= 1UL, "dirty formula evaluated");
                Expect(IsValue(sheet, 1u, 0u, 11L, 1L), "B1 = 11/1");
                Expect(sheet.VerifyChain(out nuint after) == SheetStatus.Ok,
                       "proof valid after recalculation at index " + (ulong)after);
            }
        }

        private static int Main()
        {
            SheetStatus abi = KhzAbi.Verify(out string detail);
            Console.WriteLine("abi      = " + abi + " (" + detail + ")");
            if (abi != SheetStatus.Ok)
            {
                return 2;
            }

            FormulaToFormula();
            FormulaToValue();
            DirtyFlagDoesNotInvalidateProof();

            Console.WriteLine(failures == 0 ? "ALL PASS failures=0" : "FAIL failures=" + failures);
            return failures == 0 ? 0 : 1;
        }
    }
}
