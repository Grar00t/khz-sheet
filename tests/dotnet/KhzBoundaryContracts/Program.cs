using System;
using KHZ.Sheet.Core;
internal static class Program
{
    private static int failures;
    private static void Check(bool condition, string name)
    {
        Console.WriteLine((condition ? "PASS " : "FAIL ") + name);
        if (!condition) ++failures;
    }
    private static int Main(string[] args)
    {
        int? mode = AbiBoundaryTests.Run(args);
        if (mode.HasValue) return mode.Value;
        Check(KhzAbi.Verify(out _) == SheetStatus.Ok, "ABI gate");
        if (NativeSheet.TryCreate((nuint)(1 << 20), 64, out NativeSheet? created) != SheetStatus.Ok
            || created is null) return 2;
        using NativeSheet sheet = created;
        Check(sheet.SetInt64(0, 0, 3) == SheetStatus.Ok, "seed A1");
        Check(sheet.SetInt64(1, 0, 0) == SheetStatus.Ok, "seed B1");
        Check(FormulaParser.TryParse("A1+1", out FormulaNode? node) == SheetStatus.Ok, "parse AST");
        if (node is null) return 2;
        Check(sheet.TryArenaStats(out nuint before, out _, out _, out _, out _) == SheetStatus.Ok, "initial arena");
        Check(sheet.TryDeclareDependencies(1, 0, node, out ulong declared) == SheetStatus.Ok
              && declared == 1, "declare persistent dependency");
        Check(sheet.TryArenaStats(out nuint after, out _, out _, out _, out _) == SheetStatus.Ok, "final arena");
        Check(after > before, "persistent dependency is not rewound with scratch IR");
        Console.WriteLine("arena_before=" + before + " arena_after=" + after);
        if (after <= before) return 1; // Do not dereference an already-invalid edge.
        Check(sheet.SetText(2, 0, new string('x', 4096)) == SheetStatus.Ok, "overwrite scratch region safely");
        Check(sheet.SetInt64(0, 0, 5) == SheetStatus.Ok, "traverse dependency after scratch reuse");
        Check(sheet.VerifyChain(out _) == SheetStatus.Ok, "proof after scratch reuse");
        sheet.Dispose();
        sheet.Dispose();
        Check(sheet.SetInt64(0, 0, 6) == SheetStatus.ErrState, "use after double dispose");
        Console.WriteLine("failures=" + failures);
        return failures == 0 ? 0 : 1;
    }
}
