using System.Data;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
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
    Percent2 = 10
}

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
    double? BorderThickness = null)
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
        overlay.BorderThickness ?? BorderThickness);

    public bool IsEmpty =>
        FontFamily is null && FontSize is null && Bold is null && Italic is null &&
        Underline is null && Foreground is null && Background is null &&
        Alignment is null && WrapText is null && NumberFormat is null &&
        Border is null && BorderThickness is null;
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
        if (cell.IsSelected)
        {
            cell.Background = Brush("#1F6FEB");
            cell.Foreground = Brush("#FFFFFF");
        }
    }

    private static void Reset(DataGridCell cell)
    {
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
    }

    internal static string FormatDisplayedText(string text, CellNumberFormat? format)
    {
        if (format is null || format == CellNumberFormat.General) return text;
        if (!TryDecimal(text, out decimal value)) return text;

        string pattern = format switch
        {
            CellNumberFormat.Integer => "0",
            CellNumberFormat.Decimal2 => "0.00",
            CellNumberFormat.Thousands => "#,##0",
            CellNumberFormat.Thousands2 => "#,##0.00",
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
