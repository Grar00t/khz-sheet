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

Console.WriteLine("failures=0");
