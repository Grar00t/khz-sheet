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

var formatted = new (int Row, int Column)[] { (0, 0), (0, 1) };
Expect(sheet.ApplyFormat(formatted, new CellFormat(FontFamily: "Consolas", FontSize: 14, Bold: true,
    Foreground: "#FFFFFF", Background: "#163A5F", Alignment: CellTextAlignment.Center), out _), "apply cell format");
CellFormat a1 = sheet.GetEffectiveFormat(0, 0);
Expect(a1.FontFamily == "Consolas" && a1.FontSize == 14 && a1.Bold == true, "font format stored");
Expect(a1.Foreground == "#FFFFFF" && a1.Background == "#163A5F", "color format stored");
Expect(a1.Alignment == CellTextAlignment.Center, "alignment stored");
Expect(sheet.Undo(out _), "undo format succeeds");
Expect(sheet.GetEffectiveFormat(0, 0).IsEmpty, "undo removes explicit format");
Expect(sheet.Redo(out _), "redo format succeeds");
Expect(sheet.GetEffectiveFormat(0, 0).Bold == true, "redo restores format");

CellRange tableRange = new(0, 0, 2, 2);
Expect(sheet.ApplyTableFormat(tableRange, out _), "apply table style");
Expect(sheet.GetEffectiveFormat(0, 2).Background == "#1F6FEB", "table header format");
Expect(sheet.GetEffectiveFormat(1, 2).Background == "#162230", "table band format");
Expect(sheet.Undo(out _), "undo table style");
Expect(sheet.GetEffectiveFormat(1, 2).IsEmpty, "table style removed by undo");

Console.WriteLine("failures=0");
