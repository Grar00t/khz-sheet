using System.Data;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace KHZ.Sheet.Desktop;

public enum CellTextAlignment
{
    Left = 0,
    Center = 1,
    Right = 2
}

public enum CellNumberFormat
{
    General = 0,
    Integer = 1,
    Decimal2 = 2,
    Thousands = 3,
    Thousands2 = 4,
    Percent = 9,
    Percent2 = 10,
    Fraction = 164,
    Currency = 165,
    IsoDate = 166
}

public enum CellVerticalAlignment { Top, Center, Bottom }
public sealed record CellBorder(string Style, string Color);

public static class CellFormatDefaults
{
    public const string FontFamily = "Segoe UI";
    public const double FontSize = 11.0;
}

public sealed record CellFormat(
    string? FontFamily = null,
    double? FontSize = null,
    bool? Bold = null,
    bool? Italic = null,
    bool? Underline = null,
    string? Foreground = null,
    string? Background = null,
    CellTextAlignment? Alignment = null,
    bool? WrapText = null,
    CellNumberFormat? NumberFormat = null,
    string? Border = null,
    double? BorderThickness = null,
    CellVerticalAlignment? VerticalAlignment = null,
    CellBorder? LeftBorder = null, CellBorder? RightBorder = null,
    CellBorder? TopBorder = null, CellBorder? BottomBorder = null)
{
    public static CellFormat Empty { get; } = new();

    public CellFormat Merge(CellFormat overlay) => new(
        overlay.FontFamily ?? FontFamily,
        overlay.FontSize ?? FontSize,
        overlay.Bold ?? Bold,
        overlay.Italic ?? Italic,
        overlay.Underline ?? Underline,
        overlay.Foreground ?? Foreground,
        overlay.Background ?? Background,
        overlay.Alignment ?? Alignment,
        overlay.WrapText ?? WrapText,
        overlay.NumberFormat ?? NumberFormat,
        overlay.Border ?? Border,
        overlay.BorderThickness ?? BorderThickness,
        overlay.VerticalAlignment ?? VerticalAlignment,
        overlay.LeftBorder ?? LeftBorder, overlay.RightBorder ?? RightBorder,
        overlay.TopBorder ?? TopBorder, overlay.BottomBorder ?? BottomBorder);

    public bool IsEmpty =>
        FontFamily is null && FontSize is null && Bold is null && Italic is null &&
        Underline is null && Foreground is null && Background is null &&
        Alignment is null && WrapText is null && NumberFormat is null &&
        Border is null && BorderThickness is null && VerticalAlignment is null &&
        LeftBorder is null && RightBorder is null && TopBorder is null && BottomBorder is null;

    public bool IsValid => (FontFamily is null || FontFamily.Length is > 0 and <= 128) &&
        (FontSize is null || (double.IsFinite(FontSize.Value) && FontSize is >= 1 and <= 409)) &&
        (BorderThickness is null || BorderThickness is >= 0 and <= 4) &&
        (Alignment is null || Enum.IsDefined(Alignment.Value)) &&
        (VerticalAlignment is null || Enum.IsDefined(VerticalAlignment.Value)) &&
        (NumberFormat is null || Enum.IsDefined(NumberFormat.Value)) &&
        ValidColor(Foreground) && ValidColor(Background) && ValidColor(Border) &&
        new[] {LeftBorder, RightBorder, TopBorder, BottomBorder}.All(b => b is null ||
            (b.Style is "thin" or "medium" or "double" or "thick" or "none" && ValidColor(b.Color)));
    private static bool ValidColor(string? color) => color is null ||
        (color.StartsWith('#') && (color.Length == 7 || color.Length == 9) && color[1..].All(Uri.IsHexDigit));
}

public readonly record struct CellRange(int StartRow, int StartColumn, int EndRow, int EndColumn)
{
    public bool Contains(int row, int column) =>
        row >= StartRow && row <= EndRow && column >= StartColumn && column <= EndColumn;
}

public readonly record struct CellPresentation(int Row, int Column, CellFormat Format);

public sealed record TableFormat(Guid Id, CellRange Range)
{
    public CellFormat FormatFor(int row)
    {
        if (row == Range.StartRow)
        {
            return new CellFormat(
                Bold: true,
                Foreground: "#FFFFFF",
                Background: "#1F6FEB",
                Border: "#58A6FF",
                BorderThickness: 1.0);
        }

        string fill = ((row - Range.StartRow) & 1) == 0 ? "#101820" : "#162230";
        return new CellFormat(Background: fill, Border: "#30363D", BorderThickness: 1.0);
    }
}

public static class CellVisualFormat
{
    public static void Apply(DataGridCell cell, CellFormat format, string? baseText = null)
    {
        Reset(cell);
        if (format.FontFamily is not null) cell.FontFamily = new FontFamily(format.FontFamily);
        if (format.FontSize is double size) cell.FontSize = size;
        if (format.Bold is bool bold) cell.FontWeight = bold ? FontWeights.Bold : FontWeights.Normal;
        if (format.Italic is bool italic) cell.FontStyle = italic ? FontStyles.Italic : FontStyles.Normal;
        if (cell.Content is TextBlock text)
        {
            if (baseText is not null) text.Text = FormatDisplayedText(baseText, format.NumberFormat);
            if (format.Underline is bool underline)
                text.TextDecorations = underline ? TextDecorations.Underline : null;
            if (format.WrapText is bool wrap)
                text.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        }
        if (format.Foreground is not null) cell.Foreground = Brush(format.Foreground);
        if (format.Background is not null) cell.Background = Brush(format.Background);
        if (format.Alignment is CellTextAlignment alignment)
            cell.HorizontalContentAlignment = alignment switch
            {
                CellTextAlignment.Center => HorizontalAlignment.Center,
                CellTextAlignment.Right => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Left
            };
        if (format.Border is not null) cell.BorderBrush = Brush(format.Border);
        if (format.BorderThickness is double thickness) cell.BorderThickness = new Thickness(thickness);
        if (format.LeftBorder is not null || format.RightBorder is not null || format.TopBorder is not null || format.BottomBorder is not null)
        {
            AdornerLayer? layer = AdornerLayer.GetAdornerLayer(cell);
            if (layer is not null) layer.Add(new CellBorderAdorner(cell, format));
        }
        if (format.VerticalAlignment is CellVerticalAlignment vertical)
            cell.VerticalContentAlignment = vertical switch { CellVerticalAlignment.Top => System.Windows.VerticalAlignment.Top,
                CellVerticalAlignment.Center => System.Windows.VerticalAlignment.Center, _ => System.Windows.VerticalAlignment.Bottom };
        if (cell.IsSelected)
        {
            cell.Background = cell.TryFindResource("SelectionBrush") as Brush ?? Brush("#1F6FEB");
            cell.Foreground = cell.TryFindResource("SelectedTextBrush") as Brush ?? Brushes.White;
        }
    }

    private static void Reset(DataGridCell cell)
    {
        AdornerLayer? layer = AdornerLayer.GetAdornerLayer(cell);
        foreach (var adorner in layer?.GetAdorners(cell)?.OfType<CellBorderAdorner>().ToArray() ?? []) layer!.Remove(adorner);
        cell.ClearValue(Control.FontFamilyProperty);
        cell.ClearValue(Control.FontSizeProperty);
        cell.ClearValue(Control.FontWeightProperty);
        cell.ClearValue(Control.FontStyleProperty);
        if (cell.Content is TextBlock text)
        {
            text.ClearValue(TextBlock.TextDecorationsProperty);
            text.ClearValue(TextBlock.TextWrappingProperty);
        }
        cell.ClearValue(Control.ForegroundProperty);
        cell.ClearValue(Control.BackgroundProperty);
        cell.ClearValue(Control.HorizontalContentAlignmentProperty);
        cell.ClearValue(Control.BorderBrushProperty);
        cell.ClearValue(Control.BorderThicknessProperty);
        cell.ClearValue(Control.VerticalContentAlignmentProperty);
    }

    private sealed class CellBorderAdorner : Adorner
    {
        private readonly CellFormat _format;
        public CellBorderAdorner(DataGridCell cell, CellFormat format) : base(cell)
        { _format = format; IsHitTestVisible = false; }
        protected override void OnRender(DrawingContext context)
        {
            double w = AdornedElement.RenderSize.Width, h = AdornedElement.RenderSize.Height;
            void Edge(CellBorder? border, bool horizontal, bool far)
            {
                if (border is null || w < 4 || h < 4) return;
                double width = border.Style == "medium" ? 2 : 1;
                var pen = new Pen(Brush(border.Color), width);
                void Line(double inset)
                {
                    double at = far ? (horizontal ? h : w) - inset : inset;
                    context.DrawLine(pen, horizontal ? new Point(0, at) : new Point(at, 0),
                        horizontal ? new Point(w, at) : new Point(at, h));
                }
                Line(width / 2);
                if (border.Style == "double") Line(2.5);
            }
            Edge(_format.LeftBorder, false, false); Edge(_format.RightBorder, false, true);
            Edge(_format.TopBorder, true, false); Edge(_format.BottomBorder, true, true);
        }
    }

    internal static string FormatDisplayedText(string text, CellNumberFormat? format)
    {
        if (format is null || format is CellNumberFormat.General or CellNumberFormat.Fraction) return text;
        if (!TryDecimal(text, out decimal value)) return text;

        if (format == CellNumberFormat.IsoDate)
        {
            // Excel's 1900 epoch; the fictitious leap day is displayed explicitly.
            if (value == 60) return "1900-02-29";
            if (value < 1 || value > 2958465) return text;
            return new DateTime(1899, 12, 31).AddDays((double)(value >= 60 ? value - 1 : value)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        string pattern = format switch
        {
            CellNumberFormat.Integer => "0",
            CellNumberFormat.Decimal2 => "0.00",
            CellNumberFormat.Thousands => "#,##0",
            CellNumberFormat.Thousands2 => "#,##0.00",
            CellNumberFormat.Currency => "\"USD \"#,##0.00",
            CellNumberFormat.Percent => "0%",
            CellNumberFormat.Percent2 => "0.00%",
            _ => "G"
        };
        return value.ToString(pattern, CultureInfo.InvariantCulture);
    }

    private static bool TryDecimal(string text, out decimal value)
    {
        int slash = text.IndexOf('/');
        if (slash > 0 && slash == text.LastIndexOf('/'))
        {
            if (decimal.TryParse(text[..slash], NumberStyles.Integer, CultureInfo.InvariantCulture, out decimal n) &&
                decimal.TryParse(text[(slash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out decimal d) && d != 0)
            {
                value = n / d;
                return true;
            }
        }
        return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static SolidColorBrush Brush(string text)
    {
        object converted = ColorConverter.ConvertFromString(text)
            ?? throw new FormatException($"invalid color {text}");
        SolidColorBrush brush = new((Color)converted);
        brush.Freeze();
        return brush;
    }
}
