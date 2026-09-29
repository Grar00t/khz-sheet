using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using KHZ.Sheet.Core;

namespace KHZ.Sheet.Desktop;

internal static class XlsxPresentationSerializer
{
    private const string ContentTypesPart = "[Content_Types].xml";
    private const string WorkbookRelsPart = "xl/_rels/workbook.xml.rels";
    private const string SheetPart = "xl/worksheets/sheet1.xml";
    private const string StylesPart = "xl/styles.xml";
    private const string StylesContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml";
    private const string StylesRelationship =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles";
    private const long MaxXmlCharacters = 16L * 1024L * 1024L;

    private readonly record struct FontKey(
        string Family, double Size, bool Bold, bool Italic, bool Underline, string? Color);

    private readonly record struct BorderKey(string Color, string Style);
    public static bool TryApply(
        string sourcePath,
        string destinationPath,
        IReadOnlyList<CellPresentation> cells,
        out string message)
    {
        message = string.Empty;
        if (cells.Count == 0)
        {
            message = "no presentation styles";
            return false;
        }

        try
        {
            using FileStream source = File.OpenRead(sourcePath);
            SheetStatus opened = OpcPackage.TryOpen(source, out OpcPackage? package);
            if (opened != SheetStatus.Ok || package is null)
            {
                message = SheetStatusText.Name(opened);
                return false;
            }

            using (package)
            {
                if (package.DroppedEntries.Count != 0)
                {
                    message = "generated xlsx contained dropped OPC entries";
                    return false;
                }
                if (package.TryOpenPart(StylesPart, out Stream? existingStyles) == SheetStatus.Ok)
                {
                    existingStyles?.Dispose();
                    message = "generated xlsx unexpectedly already contains styles.xml";
                    return false;
                }

                if (!TryReadDocument(package, ContentTypesPart, out XDocument? contentTypes) ||
                    !TryReadDocument(package, WorkbookRelsPart, out XDocument? workbookRels) ||
                    !TryReadDocument(package, SheetPart, out XDocument? sheet))
                {
                    message = "generated xlsx is missing a required presentation part";
                    return false;
                }

                XDocument styles = BuildStyles(cells, out Dictionary<CellFormat, int> styleIndexes);
                if (!ApplyContentTypes(contentTypes!) ||
                    !ApplyWorkbookRelationship(workbookRels!) ||
                    !ApplyCellStyles(sheet!, cells, styleIndexes))
                {
                    message = "generated xlsx presentation structure is unsupported";
                    return false;
                }

                if (package.TryReplacePart(ContentTypesPart, Serialize(contentTypes!)) != SheetStatus.Ok ||
                    package.TryReplacePart(WorkbookRelsPart, Serialize(workbookRels!)) != SheetStatus.Ok ||
                    package.TryReplacePart(SheetPart, Serialize(sheet!)) != SheetStatus.Ok)
                {
                    message = "failed to replace generated xlsx presentation parts";
                    return false;
                }
                if (package.TryAddPart(StylesPart, Serialize(styles)) != SheetStatus.Ok)
                {
                    message = "failed to add styles.xml";
                    return false;
                }

                using FileStream destination = new(
                    destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                SheetStatus saved = package.TrySave(destination);
                if (saved != SheetStatus.Ok)
                {
                    message = SheetStatusText.Name(saved);
                    return false;
                }
            }

            message = $"styles · {cells.Count:N0} cells";
            return true;
        }
        catch (IOException ex)
        {
            message = ex.Message;
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            message = ex.Message;
            return false;
        }
        catch (XmlException ex)
        {
            message = ex.Message;
            return false;
        }
    }
    private static XDocument BuildStyles(
        IReadOnlyList<CellPresentation> cells,
        out Dictionary<CellFormat, int> styleIndexes)
    {
        XNamespace x = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        List<FontKey> fonts = [new FontKey(CellFormatDefaults.FontFamily, CellFormatDefaults.FontSize, false, false, false, null)];
        List<string> fills = [string.Empty, "gray125"];
        List<BorderKey?> borders = [null];
        Dictionary<FontKey, int> fontIds = new() { [fonts[0]] = 0 };
        Dictionary<string, int> fillIds = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<BorderKey, int> borderIds = new();
        styleIndexes = new Dictionary<CellFormat, int>();

        List<CellFormat> formats = new();
        foreach (CellPresentation cell in cells)
        {
            if (!styleIndexes.ContainsKey(cell.Format))
            {
                styleIndexes[cell.Format] = 0;
                formats.Add(cell.Format);
            }
        }

        foreach (CellFormat format in formats)
        {
            EnsureFont(format, fonts, fontIds);
            EnsureFill(format, fills, fillIds);
            EnsureBorder(format, borders, borderIds);
        }
        XElement fontsElement = new(x + "fonts", new XAttribute("count", fonts.Count));
        foreach (FontKey font in fonts)
        {
            XElement element = new(x + "font",
                new XElement(x + "sz", new XAttribute("val", Number(font.Size))),
                new XElement(x + "name", new XAttribute("val", font.Family)));
            if (font.Bold) element.AddFirst(new XElement(x + "b"));
            if (font.Italic) element.AddFirst(new XElement(x + "i"));
            if (font.Underline) element.AddFirst(new XElement(x + "u"));
            if (font.Color is not null)
                element.Add(new XElement(x + "color", new XAttribute("rgb", Argb(font.Color))));
            fontsElement.Add(element);
        }

        XElement fillsElement = new(x + "fills", new XAttribute("count", fills.Count));
        fillsElement.Add(new XElement(x + "fill",
            new XElement(x + "patternFill", new XAttribute("patternType", "none"))));
        fillsElement.Add(new XElement(x + "fill",
            new XElement(x + "patternFill", new XAttribute("patternType", "gray125"))));
        for (int i = 2; i < fills.Count; i++)
        {
            fillsElement.Add(new XElement(x + "fill",
                new XElement(x + "patternFill", new XAttribute("patternType", "solid"),
                    new XElement(x + "fgColor", new XAttribute("rgb", Argb(fills[i]))),
                    new XElement(x + "bgColor", new XAttribute("indexed", "64")))));
        }
        XElement bordersElement = new(x + "borders", new XAttribute("count", borders.Count));
        bordersElement.Add(EmptyBorder(x));
        for (int i = 1; i < borders.Count; i++)
        {
            BorderKey border = borders[i]!.Value;
            XElement element = new(x + "border");
            foreach (string side in new[] { "left", "right", "top", "bottom" })
            {
                element.Add(new XElement(x + side,
                    new XAttribute("style", border.Style),
                    new XElement(x + "color", new XAttribute("rgb", Argb(border.Color)))));
            }
            element.Add(new XElement(x + "diagonal"));
            bordersElement.Add(element);
        }

        XElement cellXfs = new(x + "cellXfs", new XAttribute("count", formats.Count + 1));
        cellXfs.Add(new XElement(x + "xf",
            new XAttribute("numFmtId", "0"),
            new XAttribute("fontId", "0"),
            new XAttribute("fillId", "0"),
            new XAttribute("borderId", "0"),
            new XAttribute("xfId", "0")));

        int styleIndex = 1;
        foreach (CellFormat format in formats)
        {
            int fontId = FontId(format, fontIds);
            int fillId = FillId(format, fillIds);
            int borderId = BorderId(format, borderIds);
            int numberFormatId = (int)(format.NumberFormat ?? CellNumberFormat.General);
            XElement xf = new(x + "xf",
                new XAttribute("numFmtId", numberFormatId),
                new XAttribute("fontId", fontId),
                new XAttribute("fillId", fillId),
                new XAttribute("borderId", borderId),
                new XAttribute("xfId", "0"));
            if (fontId != 0) xf.Add(new XAttribute("applyFont", "1"));
            if (fillId != 0) xf.Add(new XAttribute("applyFill", "1"));
            if (borderId != 0) xf.Add(new XAttribute("applyBorder", "1"));
            if (format.NumberFormat is not null) xf.Add(new XAttribute("applyNumberFormat", "1"));
            if (format.Alignment is not null || format.WrapText is not null)
            {
                xf.Add(new XAttribute("applyAlignment", "1"));
                XElement alignmentElement = new(x + "alignment");
                if (format.Alignment is CellTextAlignment alignment)
                    alignmentElement.Add(new XAttribute("horizontal", AlignmentName(alignment)));
                if (format.WrapText == true)
                    alignmentElement.Add(new XAttribute("wrapText", "1"));
                xf.Add(alignmentElement);
            }
            cellXfs.Add(xf);
            styleIndexes[format] = styleIndex++;
        }

        return new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(x + "styleSheet",
                fontsElement,
                fillsElement,
                bordersElement,
                new XElement(x + "cellStyleXfs", new XAttribute("count", "1"),
                    new XElement(x + "xf", new XAttribute("numFmtId", "0"),
                        new XAttribute("fontId", "0"), new XAttribute("fillId", "0"),
                        new XAttribute("borderId", "0"))),
                cellXfs,
                new XElement(x + "cellStyles", new XAttribute("count", "1"),
                    new XElement(x + "cellStyle", new XAttribute("name", "Normal"),
                        new XAttribute("xfId", "0"), new XAttribute("builtinId", "0"))),
                new XElement(x + "dxfs", new XAttribute("count", "0")),
                new XElement(x + "tableStyles", new XAttribute("count", "0"),
                    new XAttribute("defaultTableStyle", "TableStyleMedium2"),
                    new XAttribute("defaultPivotStyle", "PivotStyleLight16"))));
    }

    private static void EnsureFont(
        CellFormat format,
        List<FontKey> fonts,
        Dictionary<FontKey, int> ids)
    {
        if (!UsesCustomFont(format)) return;
        FontKey key = FontKeyFor(format);
        if (ids.ContainsKey(key)) return;
        ids[key] = fonts.Count;
        fonts.Add(key);
    }

    private static void EnsureFill(
        CellFormat format,
        List<string> fills,
        Dictionary<string, int> ids)
    {
        if (format.Background is null || ids.ContainsKey(format.Background)) return;
        ids[format.Background] = fills.Count;
        fills.Add(format.Background);
    }
    private static void EnsureBorder(
        CellFormat format,
        List<BorderKey?> borders,
        Dictionary<BorderKey, int> ids)
    {
        if (format.Border is null) return;
        BorderKey key = new(format.Border, BorderStyle(format.BorderThickness));
        if (ids.ContainsKey(key)) return;
        ids[key] = borders.Count;
        borders.Add(key);
    }

    private static bool UsesCustomFont(CellFormat format) =>
        format.FontFamily is not null || format.FontSize is not null ||
        format.Bold is not null || format.Italic is not null ||
        format.Underline is not null || format.Foreground is not null;

    private static FontKey FontKeyFor(CellFormat format) => new(
        format.FontFamily ?? CellFormatDefaults.FontFamily,
        format.FontSize ?? CellFormatDefaults.FontSize,
        format.Bold ?? false,
        format.Italic ?? false,
        format.Underline ?? false,
        format.Foreground);

    private static int FontId(CellFormat format, Dictionary<FontKey, int> ids) =>
        UsesCustomFont(format) ? ids[FontKeyFor(format)] : 0;

    private static int FillId(CellFormat format, Dictionary<string, int> ids) =>
        format.Background is not null ? ids[format.Background] : 0;
    private static int BorderId(CellFormat format, Dictionary<BorderKey, int> ids)
    {
        if (format.Border is null) return 0;
        return ids[new BorderKey(format.Border, BorderStyle(format.BorderThickness))];
    }

    private static string BorderStyle(double? thickness) =>
        thickness is >= 3.0 ? "thick" : thickness is >= 2.0 ? "medium" : "thin";

    private static string AlignmentName(CellTextAlignment alignment) => alignment switch
    {
        CellTextAlignment.Center => "center",
        CellTextAlignment.Right => "right",
        _ => "left"
    };

    private static XElement EmptyBorder(XNamespace x) =>
        new(x + "border",
            new XElement(x + "left"),
            new XElement(x + "right"),
            new XElement(x + "top"),
            new XElement(x + "bottom"),
            new XElement(x + "diagonal"));

    private static string Number(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);
    private static bool ApplyContentTypes(XDocument document)
    {
        XNamespace ct = "http://schemas.openxmlformats.org/package/2006/content-types";
        XElement? root = document.Root;
        if (root is null || root.Name != ct + "Types") return false;
        bool exists = root.Elements(ct + "Override").Any(x =>
            string.Equals((string?)x.Attribute("PartName"), "/xl/styles.xml", StringComparison.Ordinal));
        if (!exists)
        {
            root.Add(new XElement(ct + "Override",
                new XAttribute("PartName", "/xl/styles.xml"),
                new XAttribute("ContentType", StylesContentType)));
        }
        return true;
    }

    private static bool ApplyWorkbookRelationship(XDocument document)
    {
        XNamespace rel = "http://schemas.openxmlformats.org/package/2006/relationships";
        XElement? root = document.Root;
        if (root is null || root.Name != rel + "Relationships") return false;
        if (root.Elements(rel + "Relationship").Any(x =>
            string.Equals((string?)x.Attribute("Type"), StylesRelationship, StringComparison.Ordinal)))
            return false;

        int next = 1;
        HashSet<string> ids = root.Elements(rel + "Relationship")
            .Select(x => (string?)x.Attribute("Id"))
            .Where(x => !string.IsNullOrEmpty(x))
            .Select(x => x!)
            .ToHashSet(StringComparer.Ordinal);
        while (ids.Contains($"rId{next}")) next++;
        root.Add(new XElement(rel + "Relationship",
            new XAttribute("Id", $"rId{next}"),
            new XAttribute("Type", StylesRelationship),
            new XAttribute("Target", "styles.xml")));
        return true;
    }

    private static bool ApplyCellStyles(
        XDocument document,
        IReadOnlyList<CellPresentation> cells,
        IReadOnlyDictionary<CellFormat, int> styleIndexes)
    {
        XNamespace x = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XElement? root = document.Root;
        XElement? sheetData = root?.Element(x + "sheetData");
        if (root is null || root.Name != x + "worksheet" || sheetData is null) return false;

        Dictionary<int, XElement> rows = sheetData.Elements(x + "row")
            .Where(r => int.TryParse((string?)r.Attribute("r"), out _))
            .ToDictionary(r => int.Parse((string)r.Attribute("r")!, CultureInfo.InvariantCulture));

        foreach (CellPresentation item in cells)
        {
            int rowNumber = item.Row + 1;
            string reference = WorksheetSession.ColumnName(item.Column) + rowNumber.ToString(CultureInfo.InvariantCulture);
            if (!rows.TryGetValue(rowNumber, out XElement? row))
            {
                row = new XElement(x + "row", new XAttribute("r", rowNumber));
                rows.Add(rowNumber, row);
                sheetData.Add(row);
            }
            XElement? cell = row.Elements(x + "c").FirstOrDefault(c =>
                string.Equals((string?)c.Attribute("r"), reference, StringComparison.Ordinal));
            if (cell is null)
            {
                cell = new XElement(x + "c", new XAttribute("r", reference));
                row.Add(cell);
            }
            cell.SetAttributeValue("s", styleIndexes[item.Format]);
        }

        foreach (XElement row in rows.Values)
        {
            List<XElement> ordered = row.Elements(x + "c")
                .OrderBy(c => CellColumnIndex((string?)c.Attribute("r")))
                .ToList();
            row.ReplaceNodes(ordered);
        }

        List<XElement> orderedRows = rows.OrderBy(pair => pair.Key)
            .Select(pair => pair.Value)
            .ToList();
        sheetData.ReplaceNodes(orderedRows);
        return true;
    }

    private static int CellColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return int.MaxValue;
        int value = 0;
        foreach (char c in reference)
        {
            if (c < 'A' || c > 'Z') break;
            value = checked(value * 26 + (c - 'A' + 1));
        }
        return value - 1;
    }
    private static bool TryReadDocument(OpcPackage package, string partName, out XDocument? document)
    {
        document = null;
        if (package.TryOpenPart(partName, out Stream? stream) != SheetStatus.Ok || stream is null)
            return false;

        using (stream)
        using (XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxXmlCharacters
        }))
        {
            document = XDocument.Load(reader, LoadOptions.None);
            return true;
        }
    }

    private static byte[] Serialize(XDocument document)
    {
        using MemoryStream buffer = new();
        XmlWriterSettings settings = new()
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false,
            OmitXmlDeclaration = false,
            NewLineHandling = NewLineHandling.None
        };
        using (XmlWriter writer = XmlWriter.Create(buffer, settings)) document.Save(writer);
        return buffer.ToArray();
    }
    private static string Argb(string color)
    {
        string hex = color.Trim();
        if (hex.StartsWith('#')) hex = hex[1..];
        if (hex.Length == 6) hex = "FF" + hex;
        if (hex.Length != 8 || !hex.All(Uri.IsHexDigit))
            throw new FormatException($"invalid color {color}");
        return hex.ToUpperInvariant();
    }
}
