using System.Data;
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

public sealed record CellFormat(
    string? FontFamily = null,
    double? FontSize = null,
    bool? Bold = null,
    bool? Italic = null,
    string? Foreground = null,
    string? Background = null,
    CellTextAlignment? Alignment = null,
    string? Border = null,
    double? BorderThickness = null)
{
    public static CellFormat Empty { get; } = new();

    public CellFormat Merge(CellFormat overlay) => new(
        overlay.FontFamily ?? FontFamily,
        overlay.FontSize ?? FontSize,
        overlay.Bold ?? Bold,
        overlay.Italic ?? Italic,
        overlay.Foreground ?? Foreground,
        overlay.Background ?? Background,
        overlay.Alignment ?? Alignment,
        overlay.Border ?? Border,
        overlay.BorderThickness ?? BorderThickness);

    public bool IsEmpty =>
        FontFamily is null && FontSize is null && Bold is null && Italic is null &&
        Foreground is null && Background is null && Alignment is null &&
        Border is null && BorderThickness is null;
}

public readonly record struct CellRange(int StartRow, int StartColumn, int EndRow, int EndColumn)
{
    public bool Contains(int row, int column) =>
        row >= StartRow && row <= EndRow && column >= StartColumn && column <= EndColumn;
}

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
    public static void Apply(DataGridCell cell, CellFormat format)
    {
        Reset(cell);
        if (format.FontFamily is not null) cell.FontFamily = new FontFamily(format.FontFamily);
        if (format.FontSize is double size) cell.FontSize = size;
        if (format.Bold is bool bold) cell.FontWeight = bold ? FontWeights.Bold : FontWeights.Normal;
        if (format.Italic is bool italic) cell.FontStyle = italic ? FontStyles.Italic : FontStyles.Normal;
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
        cell.ClearValue(Control.ForegroundProperty);
        cell.ClearValue(Control.BackgroundProperty);
        cell.ClearValue(Control.HorizontalContentAlignmentProperty);
        cell.ClearValue(Control.BorderBrushProperty);
        cell.ClearValue(Control.BorderThicknessProperty);
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
