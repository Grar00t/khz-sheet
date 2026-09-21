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

        private static int Main()
        {
            SheetStatus abi = KhzAbi.Verify(out string detail);
            Console.WriteLine("abi      = " + abi + " (" + detail + ")");
            if (abi != SheetStatus.Ok)
            {
                return 2;
            }

            SheetStatus created = NativeSheet.TryCreate((nuint)(1 << 20), (nuint)128,
                                                        out NativeSheet? sheet);
            Console.WriteLine("create   = " + created);
            if (created != SheetStatus.Ok || sheet == null)
            {
                return 2;
            }

            using (sheet)
            {
                Console.WriteLine("initial dependency:");
                Expect(sheet.SetInt64(0u, 0u, 10L) == SheetStatus.Ok, "A1 = 10");
                Expect(Install(sheet, 1u, 0u, "A1") == SheetStatus.Ok, "B1 = A1");
                Expect(sheet.TryRecalculate(out ulong first) == SheetStatus.Ok,
                       "first recalc");
                Expect(first >= 1UL, "first recalc evaluated formula");
                Expect(IsValue(sheet, 1u, 0u, 10L, 1L), "B1 = 10/1");

                Console.WriteLine("rewrite dependent to constant:");
                Expect(Install(sheet, 1u, 0u, "1") == SheetStatus.Ok, "B1 = 1");
                Expect(sheet.TryRecalculate(out ulong second) == SheetStatus.Ok,
                       "second recalc");
                Expect(second >= 1UL, "second recalc evaluated rewritten formula");
                Expect(IsValue(sheet, 1u, 0u, 1L, 1L), "B1 = 1/1");

                Console.WriteLine("reverse dependency after rewrite:");
                Expect(Install(sheet, 0u, 0u, "B1") == SheetStatus.Ok, "A1 = B1");
                SheetStatus thirdStatus = sheet.TryRecalculate(out ulong third);
                Expect(thirdStatus == SheetStatus.Ok,
                       "third recalc has no stale-edge cycle -> " + thirdStatus);
                if (thirdStatus == SheetStatus.Ok)
                {
                    Expect(third >= 1UL, "third recalc evaluated formula");
                    Expect(IsValue(sheet, 0u, 0u, 1L, 1L), "A1 = 1/1");
                    Expect(IsValue(sheet, 1u, 0u, 1L, 1L), "B1 remains 1/1");
                }

                Expect(sheet.VerifyChain(out nuint failedIndex) == SheetStatus.Ok,
                       "proof chain verifies at index " + (ulong)failedIndex);
            }

            Console.WriteLine(failures == 0 ? "ALL PASS failures=0" : "FAIL failures=" + failures);
            return failures == 0 ? 0 : 1;
        }
    }
}
