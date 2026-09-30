using System;
using System.Collections.Generic;
using System.Linq;

namespace KHZ.Sheet.Core;

public enum ChartKind { Bar, Column, Line }
public sealed record ChartDefinition(Guid Id, string Title, ChartKind Kind, CellRange Categories, CellRange Values)
{
    public bool IsValid => Enum.IsDefined(Kind) && Title.Length <= 255 &&
        Categories.ColumnCount == 1 && Values.ColumnCount == 1 &&
        Categories.RowCount == Values.RowCount && Categories.RowCount is > 0 and <= 4096;
}
public sealed record ChartPoint(string Category, KhzRational? Value);
public sealed record TableDefinition(Guid Id, string Name, CellRange Range, IReadOnlyList<string> Columns, bool Totals)
{
    public bool IsValid => Name.Length is > 0 and <= 64 &&
        (char.IsAsciiLetter(Name[0]) || Name[0] == '_') && Name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') &&
        CellAddress.TryParse(Name, out _) != SheetStatus.Ok && Range.RowCount >= (Totals ? 3 : 2) &&
        Columns.Count == Range.ColumnCount && Columns.All(c => c.Length is > 0 and <= 255) &&
        Columns.Distinct(StringComparer.OrdinalIgnoreCase).Count() == Columns.Count && Range.CellCount <= 200000;
}
public sealed record SheetTheme(string Name, string Background, string Text, string Panel, string Accent, string Band, string Grid)
{
    public static IReadOnlyList<SheetTheme> Presets { get; } = Array.AsReadOnly(new[] {
        new SheetTheme("Classic Light", "#FFFFFF", "#17212B", "#F1F3F5", "#216E39", "#F2F6FA", "#CBD2D9"),
        new SheetTheme("Monochromatic Dark", "#111111", "#EEEEEE", "#242424", "#666666", "#1B1B1B", "#555555"),
        new SheetTheme("Corporate Blue", "#FFFFFF", "#152B4B", "#EAF0F8", "#1F6FEB", "#E6EFFC", "#9AB7DD"),
        new SheetTheme("High Contrast", "#000000", "#FFFFFF", "#000000", "#FFFF00", "#202020", "#FFFFFF") });
}
