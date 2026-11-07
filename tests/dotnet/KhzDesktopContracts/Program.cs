using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using KHZ.Sheet.Desktop;

static void Expect(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("FAIL " + label);
    Console.WriteLine("PASS " + label);
}

static string ReadEntry(ZipArchive archive, string name)
{
    ZipArchiveEntry entry = archive.GetEntry(name)
        ?? throw new InvalidOperationException("missing entry " + name);
    using StreamReader reader = new(entry.Open());
    return reader.ReadToEnd();
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
Expect(sheet.TryFindInput("sum", int.MaxValue, int.MaxValue, out row, out column)
    && row == 2 && column == 3, "find invalid start coordinates do not overflow");
Expect(!sheet.TryFindInput("not-present", -1, -1, out _, out _), "missing query returns false");

var formatted = new (int Row, int Column)[] { (0, 0), (0, 1) };
Expect(sheet.ApplyFormat(formatted, new CellFormat(FontFamily: "Consolas", FontSize: 14, Bold: true,
    Underline: true, Foreground: "#FFFFFF", Background: "#163A5F",
    Alignment: CellTextAlignment.Center, WrapText: true, NumberFormat: CellNumberFormat.Decimal2), out _), "apply cell format");
CellFormat a1 = sheet.GetEffectiveFormat(0, 0);
Expect(a1.FontFamily == "Consolas" && a1.FontSize == 14 && a1.Bold == true, "font format stored");
Expect(a1.Foreground == "#FFFFFF" && a1.Background == "#163A5F", "color format stored");
Expect(a1.Alignment == CellTextAlignment.Center, "alignment stored");
Expect(a1.Underline == true && a1.WrapText == true, "underline and wrap stored");
Expect(a1.NumberFormat == CellNumberFormat.Decimal2, "number format stored");

Exception? visualError = null;
Thread visualThread = new(() =>
{
    try
    {
        DataGridCell visualCell = new() { Content = new TextBlock() };
        CellVisualFormat.Apply(visualCell, a1, "1/2");
        TextBlock visualText = (TextBlock)visualCell.Content;
        Expect(visualText.Text == "0.50", "number format renders locally");
        Expect(visualText.TextDecorations?.Count > 0, "underline renders locally");
        Expect(visualText.TextWrapping == TextWrapping.Wrap, "wrap renders locally");
    }
    catch (Exception ex) { visualError = ex; }
});
visualThread.SetApartmentState(ApartmentState.STA);
visualThread.Start();
visualThread.Join();
if (visualError is not null) throw visualError;

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
Expect(sheet.Redo(out _), "redo table style for export");

string exportPath = Path.Combine(Path.GetTempPath(), $"khz-style-contract-{Guid.NewGuid():N}.xlsx");
try
{
    Expect(sheet.ExportXlsx(exportPath, out string exportMessage), "export styled xlsx");
    Expect(exportMessage.Contains("styles", StringComparison.Ordinal), "export reports style serialization");
    using ZipArchive archive = ZipFile.OpenRead(exportPath);
    Expect(archive.GetEntry("xl/styles.xml") is not null, "styles.xml emitted");

    XNamespace x = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    XDocument styles = XDocument.Parse(ReadEntry(archive, "xl/styles.xml"));
    XDocument worksheet = XDocument.Parse(ReadEntry(archive, "xl/worksheets/sheet1.xml"));
    string contentTypes = ReadEntry(archive, "[Content_Types].xml");
    string workbookRels = ReadEntry(archive, "xl/_rels/workbook.xml.rels");

    Expect(styles.Root?.Name == x + "styleSheet", "styles.xml root valid");
    Expect(contentTypes.Contains("/xl/styles.xml", StringComparison.Ordinal), "styles content type linked");
    Expect(workbookRels.Contains("/relationships/styles", StringComparison.Ordinal), "styles relationship linked");
    XElement? a1Cell = worksheet.Descendants(x + "c").FirstOrDefault(c => (string?)c.Attribute("r") == "A1");
    XElement? b2Cell = worksheet.Descendants(x + "c").FirstOrDefault(c => (string?)c.Attribute("r") == "B2");
    XElement? c1Cell = worksheet.Descendants(x + "c").FirstOrDefault(c => (string?)c.Attribute("r") == "C1");
    Expect(a1Cell?.Attribute("s") is not null, "existing formatted cell has style index");
    Expect(b2Cell?.Attribute("s") is not null, "empty table cell persisted with style index");
    Expect(c1Cell?.Attribute("s") is not null, "table header cell has style index");

    int a1StyleIndex = int.Parse((string)a1Cell!.Attribute("s")!, System.Globalization.CultureInfo.InvariantCulture);
    XElement a1Xf = styles.Root!.Element(x + "cellXfs")!.Elements(x + "xf").ElementAt(a1StyleIndex);
    Expect((string?)a1Xf.Attribute("numFmtId") == "2", "xlsx number format id persisted");
    Expect(a1Xf.Element(x + "alignment")?.Attribute("wrapText")?.Value == "1", "xlsx wrap persisted");
    int fontId = int.Parse((string)a1Xf.Attribute("fontId")!, System.Globalization.CultureInfo.InvariantCulture);
    XElement font = styles.Root.Element(x + "fonts")!.Elements(x + "font").ElementAt(fontId);
    Expect(font.Element(x + "u") is not null, "xlsx underline persisted");

    int c1StyleIndex = int.Parse((string)c1Cell!.Attribute("s")!, System.Globalization.CultureInfo.InvariantCulture);
    XElement c1Xf = styles.Root.Element(x + "cellXfs")!.Elements(x + "xf").ElementAt(c1StyleIndex);
    int c1FontId = int.Parse((string)c1Xf.Attribute("fontId")!, System.Globalization.CultureInfo.InvariantCulture);
    XElement c1Font = styles.Root.Element(x + "fonts")!.Elements(x + "font").ElementAt(c1FontId);
    Expect((string?)c1Font.Element(x + "name")?.Attribute("val") == CellFormatDefaults.FontFamily,
        "xlsx implicit formatted font matches desktop default");
}
finally
{
    if (File.Exists(exportPath)) File.Delete(exportPath);
}

using WorksheetSession csv = new("CsvSafetyRegression");
Expect(csv.EngineAvailable, "csv regression native engine");
Expect(csv.CommitCell(0, 0, "7", out _), "csv baseline value");
string csvPath = Path.Combine(Path.GetTempPath(), $"khz-csv-safety-{Guid.NewGuid():N}.csv");
try
{
    File.WriteAllText(csvPath, "9,2\r\n=SUM(A1:B1),\"hello,world\"\r\n");
    Expect(csv.LoadCsv(csvPath, out _), "csv valid staged import");
    Expect(csv.GetInput(0, 0) == "9" && csv.GetInput(0, 1) == "2", "csv staged values installed");
    Expect(csv.GetInput(1, 0) == "=SUM(A1:B1)" &&
        csv.GetInput(1, 1) == "hello,world", "csv formula and quoted comma retained");
    Expect(!csv.Undo(out _), "csv load resets stale undo history");

    ulong stableCommits = csv.NativeCommits;
    File.WriteAllText(csvPath, "11,22\r\n\"broken\"tail");
    Expect(!csv.LoadCsv(csvPath, out _), "csv rejects garbage after closing quote");
    Expect(csv.GetInput(0, 0) == "9" && csv.NativeCommits == stableCommits,
        "malformed csv leaves native commits untouched");

    File.WriteAllText(csvPath, "11,22\r\n\"unterminated");
    Expect(!csv.LoadCsv(csvPath, out _), "csv rejects unclosed quoted field");
    Expect(csv.GetInput(0, 0) == "9" && csv.NativeCommits == stableCommits,
        "unclosed csv leaves native commits untouched");

    File.WriteAllText(csvPath, string.Join(",", Enumerable.Repeat("1", 53)));
    Expect(!csv.LoadCsv(csvPath, out string columnLimit) && columnLimit.Contains("column limit"),
        "csv refuses silent column truncation");
    File.WriteAllText(csvPath, string.Join("\n", Enumerable.Repeat("1", 257)));
    Expect(!csv.LoadCsv(csvPath, out string rowLimit) && rowLimit.Contains("row limit"),
        "csv refuses silent row truncation");
    File.WriteAllText(csvPath, new string('x', 8193));
    Expect(!csv.LoadCsv(csvPath, out string cellLimit) && cellLimit.Contains("too long"),
        "csv rejects overlong cell");
    File.WriteAllText(csvPath, "11,22\n=ABS(");
    Expect(!csv.LoadCsv(csvPath, out _), "csv invalid formula is atomic");
    Expect(csv.GetInput(0, 0) == "9" && csv.NativeCommits == stableCommits,
        "failed csv transactions preserve existing sheet");
}
finally
{
    if (File.Exists(csvPath)) File.Delete(csvPath);
}

using WorksheetSession paste = new("AtomicPasteRegression");
Expect(paste.EngineAvailable, "paste native engine available");
Expect(paste.CommitCell(0, 0, "5", out _), "paste baseline value");
Expect(paste.PasteCells(0, 0, "10\t20\r\n30\t=SUM(A1:B1)", out string pasteMessage),
    "two-row paste succeeds");
Expect(paste.GetInput(0, 0) == "10" && paste.GetInput(0, 1) == "20" &&
    paste.GetInput(1, 0) == "30" && paste.GetInput(1, 1) == "=SUM(A1:B1)",
    "rectangular paste values and formulas committed");
Expect(pasteMessage.Contains("atomically", StringComparison.Ordinal), "paste reports atomic success");
Expect(paste.Undo(out _) && paste.GetInput(0, 0) == "5" &&
    paste.GetInput(0, 1) == "" && paste.GetInput(1, 0) == "",
    "one undo restores all pasted cells");
Expect(paste.Redo(out _) && paste.GetInput(0, 0) == "10" &&
    paste.GetInput(1, 1) == "=SUM(A1:B1)",
    "one redo reapplies all pasted cells");

ulong pasteCommits = paste.NativeCommits;
Expect(!paste.PasteCells(0, 51, "99\t100", out string pastedBeyondColumns) &&
    pastedBeyondColumns.Contains("column limit", StringComparison.Ordinal),
    "overflowing paste refuses all columns");
Expect(!paste.PasteCells(255, 0, "99\n100", out string pastedBeyondRows) &&
    pastedBeyondRows.Contains("row limit", StringComparison.Ordinal),
    "overflowing paste refuses all rows");
Expect(!paste.PasteCells(0, 0, "99\t=ABS(", out _),
    "paste rejects malformed formula");
Expect(!paste.PasteCells(0, 0, new string('x', 8193), out _),
    "paste rejects overlong cell");
Expect(paste.GetInput(0, 0) == "10" &&
    paste.GetInput(0, 1) == "20" &&
    paste.NativeCommits == pasteCommits,
    "rejected pastes preserve original values and native commits");
Expect(paste.Undo(out _) && paste.GetInput(0, 0) == "5",
    "rejected paste does not clear previous valid undo history");

string safeCsvPath = Path.Combine(Path.GetTempPath(), $"khz-atomic-save-{Guid.NewGuid():N}.csv");
try
{
    File.WriteAllText(safeCsvPath, "previous-valid-file");
    paste.SaveCsv(safeCsvPath);
    string savedCsv = File.ReadAllText(safeCsvPath);
    using WorksheetSession reopenedCsv = new("SavedCsvRoundTrip");
    Expect(reopenedCsv.LoadCsv(safeCsvPath, out _), "saved CSV can be reopened");
    Expect(reopenedCsv.GetInput(0, 0) == "5" &&
        reopenedCsv.GetInput(0, 1) == "" &&
        reopenedCsv.GetInput(1, 0) == "",
        "CSV staged save preserves the post-undo worksheet values");
    Expect(!savedCsv.Contains("previous-valid-file", StringComparison.Ordinal),
        "CSV successful publish replaces old content");
    string pattern = "." + Path.GetFileName(safeCsvPath) + ".*.khz.tmp";
    Expect(Directory.GetFiles(Path.GetDirectoryName(safeCsvPath)!, pattern).Length == 0,
        "CSV stage file is not left behind");
}
finally
{
    if (File.Exists(safeCsvPath)) File.Delete(safeCsvPath);
}

Console.WriteLine("failures=0");
