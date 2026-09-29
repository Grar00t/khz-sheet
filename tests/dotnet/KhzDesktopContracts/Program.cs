using KHZ.Sheet.Desktop;

static void Expect(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("FAIL " + label);
    Console.WriteLine("PASS " + label);
}

using WorksheetSession sheet = new("Contract");
Expect(sheet.EngineAvailable, "native engine available");
Expect(sheet.CommitCell(0, 0, "41", out _), "commit A1=41");
Expect(sheet.CommitCell(0, 0, "42", out _), "commit A1=42");
Expect(sheet.GetInput(0, 0) == "42", "A1 current input");
Expect(sheet.Undo(out _), "undo succeeds");
Expect(sheet.GetInput(0, 0) == "41", "undo restores input");
Expect(sheet.Redo(out _), "redo succeeds");
Expect(sheet.GetInput(0, 0) == "42", "redo restores input");

Expect(sheet.CommitCell(2, 3, "=SUM(A1:A2)", out _), "commit D3 formula");
Expect(sheet.TryFindInput("sum", -1, -1, out int row, out int column), "find formula text");
Expect(row == 2 && column == 3, "find returns D3");
Expect(!sheet.TryFindInput("not-present", -1, -1, out _, out _), "missing query returns false");

Console.WriteLine("failures=0");
