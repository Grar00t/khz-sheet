using System.Data;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using KHZ.Sheet.Core;
using CoreRange = KHZ.Sheet.Core.CellRange;

namespace KHZ.Sheet.Desktop;

public sealed partial class WorksheetSession
{
    private readonly List<TableDefinition> _tables = new();
    private readonly List<ChartDefinition> _charts = new();
    private readonly Dictionary<int,double> _columnWidths = new();
    private readonly Dictionary<int,double> _rowHeights = new();
    public IReadOnlyList<TableDefinition> Tables => _tables.AsReadOnly();
    public IReadOnlyList<ChartDefinition> Charts => _charts.AsReadOnly();
    public IReadOnlyDictionary<int,double> ColumnWidths => _columnWidths;
    public IReadOnlyDictionary<int,double> RowHeights => _rowHeights;
    public SheetTheme? Theme { get; private set; }
    public int FrozenColumns { get; private set; }
    internal bool HasWorkbookObjects => Theme is not null || Tables.Count != 0 || Charts.Count != 0 ||
        ColumnWidths.Count != 0 || RowHeights.Count != 0 || FrozenColumns != 0;
    public bool SetColumnWidth(int column, double width)
    {
        if (column < 0 || column >= Grid.Columns.Count || !double.IsFinite(width) || width is < 1 or > 255) return false;
        _columnWidths[column] = width; return true;
    }
    public bool SetRowHeight(int row, double height)
    {
        if (row < 0 || row >= Grid.Rows.Count || !double.IsFinite(height) || height is < 1 or > 409) return false;
        _rowHeights[row] = height; return true;
    }
    public void SetTheme(SheetTheme theme)
    {
        if (!SheetTheme.Presets.Contains(theme)) throw new ArgumentException("Unknown theme", nameof(theme));
        Theme = theme;
    }
    public void SetFrozenColumns(int columns)
    {
        if (columns < 0 || columns >= Grid.Columns.Count) throw new ArgumentOutOfRangeException(nameof(columns));
        FrozenColumns = columns;
    }
    private sealed record ObjectEdit(TableDefinition? Table, ChartDefinition? Chart) : WorksheetEdit;
    private sealed record TableTotalsEdit(TableDefinition Table, IReadOnlyList<CellEdit> Cells) : WorksheetEdit;

    private bool StageValues(IReadOnlyList<CellEdit> changes, bool undo, out WorksheetSession? candidate, out string message)
    {
        candidate = new WorksheetSession(Name);
        var desired = new Dictionary<long, string>(_inputs);
        foreach (var edit in changes) desired[Key(edit.Row, edit.Column)] = undo ? edit.Before : edit.After;
        foreach (var pair in desired)
        {
            DecodeKey(pair.Key, out int row, out int column);
            if (candidate.ImportValue(row, column, pair.Value, false, out message)) continue;
            candidate.Dispose(); candidate = null; return false;
        }
        if (candidate.Recalculate(out message)) return true;
        candidate.Dispose(); candidate = null; return false;
    }

    private void AdoptValues(WorksheetSession candidate)
    {
        _native?.Dispose(); _native = candidate._native; candidate._native = null;
        _inputs.Clear(); foreach (var item in candidate._inputs) _inputs.Add(item.Key, item.Value);
        RefreshComputedCells();
    }

    private bool ApplyTotalsHistory(TableTotalsEdit edit, bool undo, out string message)
    {
        if (!StageValues(edit.Cells, undo, out var candidate, out message)) return false;
        using (candidate) AdoptValues(candidate!);
        if (undo) _tables.RemoveAll(t => t.Id == edit.Table.Id); else _tables.Add(edit.Table);
        message = undo ? "undo table and totals" : "redo table and totals";
        return true;
    }

    public bool CreateTableWithTotals(CellRange selection, string name, out string message)
    {
        message = "Select headers and data with an empty row below for totals";
        if (!EngineAvailable || selection.StartRow >= selection.EndRow || selection.StartColumn > selection.EndColumn ||
            !InBounds(selection.StartRow, selection.StartColumn) || !InBounds(selection.EndRow + 1, selection.EndColumn)) return false;
        var complete = selection with { EndRow = selection.EndRow + 1 };
        if (_tables.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase) ||
            !(complete.EndRow < t.Range.TopLeft.Row || complete.StartRow > t.Range.BottomRight.Row ||
              complete.EndColumn < t.Range.TopLeft.Column || complete.StartColumn > t.Range.BottomRight.Column))) return false;
        List<CellEdit> edits = new();
        for (int c = complete.StartColumn; c <= complete.EndColumn; ++c)
        {
            string before = GetInput(complete.EndRow, c);
            if (before.Length != 0) { message = "Totals would overwrite an occupied cell"; return false; }
            string after = c == complete.StartColumn && complete.EndColumn > c ? "'Total" :
                $"=SUM({ColumnName(c)}{complete.StartRow + 2}:{ColumnName(c)}{complete.EndRow})";
            edits.Add(new CellEdit(complete.EndRow, c, before, after));
        }
        if (!StageValues(edits, false, out var candidate, out message)) return false;
        using (candidate)
        {
            if (!candidate!.CreateTable(complete, name, true, out message)) return false;
            var table = candidate.Tables.Single();
            AdoptValues(candidate); _tables.Add(table);
            _undo.Push(new TableTotalsEdit(table, edits)); _redo.Clear();
        }
        message = $"table {name} · exact SUM totals · undo as one action";
        return true;
    }
    private bool ApplyObjectHistory(ObjectEdit edit, bool undo, out string message)
    {
        if (edit.Table is TableDefinition table) { if (undo) _tables.RemoveAll(t => t.Id == table.Id); else _tables.Add(table); }
        if (edit.Chart is ChartDefinition chart) { if (undo) _charts.RemoveAll(c => c.Id == chart.Id); else _charts.Add(chart); }
        message = undo ? "undo workbook object" : "redo workbook object"; return true;
    }
    public bool CreateTable(CellRange selection, string name, bool totals, out string message)
    {
        message = "invalid table range or headers";
        if (_tables.Count >= 128) { message = "Table limit (128)"; return false; }
        if (!InBounds(selection.StartRow,selection.StartColumn) || !InBounds(selection.EndRow,selection.EndColumn) ||
            selection.StartRow >= selection.EndRow || selection.StartColumn > selection.EndColumn) return false;
        CoreRange.TryParse($"{ColumnName(selection.StartColumn)}{selection.StartRow+1}:{ColumnName(selection.EndColumn)}{selection.EndRow+1}", out var range);
        string[] names = Enumerable.Range(selection.StartColumn, range.ColumnCount)
            .Select(c => Grid.Rows[selection.StartRow][c]?.ToString() ?? "").ToArray();
        if (Enumerable.Range(selection.StartColumn, range.ColumnCount).Any(c => GetInput(selection.StartRow,c).StartsWith('=')))
        { message = "Table headers must be literal labels"; return false; }
        var table = new TableDefinition(Guid.NewGuid(),name,range,Array.AsReadOnly(names),totals);
        if (!table.IsValid || _tables.Any(t => string.Equals(t.Name,name,StringComparison.OrdinalIgnoreCase) ||
            !(range.BottomRight.Row<t.Range.TopLeft.Row || range.TopLeft.Row>t.Range.BottomRight.Row ||
              range.BottomRight.Column<t.Range.TopLeft.Column || range.TopLeft.Column>t.Range.BottomRight.Column))) return false;
        // Totals are native formulas, not presentation-only cached numbers. Require
        // them before attaching metadata, so creation cannot partially mutate cells.
        if (totals)
        {
            for (int c=range.TopLeft.Column;c<=range.BottomRight.Column;++c)
            {
                if (c == range.TopLeft.Column && range.ColumnCount > 1 &&
                    Grid.Rows[range.BottomRight.Row][c]?.ToString() == "Total" && !GetInput(range.BottomRight.Row,c).StartsWith('=')) continue;
                string expected = $"=SUM({ColumnName(c)}{range.TopLeft.Row+2}:{ColumnName(c)}{range.BottomRight.Row})";
                if (!string.Equals(GetInput(range.BottomRight.Row,c),expected,StringComparison.OrdinalIgnoreCase))
                { message = $"totals row requires {ColumnName(c)}{range.BottomRight.Row+1}: {expected}"; return false; }
            }
        }
        _tables.Add(table); _undo.Push(new ObjectEdit(table,null)); _redo.Clear();
        message = $"table {name} · {range}"; return true;
    }

    private bool ValidateTableEdit(int row, int column, string raw, out string message)
    {
        message = "";
        foreach (var table in _tables.Where(t => column >= t.Range.TopLeft.Column && column <= t.Range.BottomRight.Column))
        {
            if (table.Totals && row == table.Range.BottomRight.Row && raw != GetInput(row,column))
            { message = "Edit the data rows; table totals are calculated"; return false; }
            if (row != table.Range.TopLeft.Row) continue;
            if (raw.StartsWith('=')) { message = "Table headers must be literal labels"; return false; }
            string label = raw.StartsWith('\'') ? raw[1..] : raw;
            if (!raw.StartsWith('\''))
            {
                string trimmed = raw.Trim();
                SheetStatus status = TryParseExactNumber(trimmed, out long n, out long d, out bool numeric);
                if (numeric && status == SheetStatus.Ok) label = n.ToString(CultureInfo.InvariantCulture) + (d == 1 ? "" : "/" + d.ToString(CultureInfo.InvariantCulture));
                else if (bool.TryParse(trimmed, out bool b)) label = b ? "TRUE" : "FALSE";
                else if (TryCellError(trimmed, out var error)) label = ErrorText(error);
            }
            int index = column - table.Range.TopLeft.Column;
            if (string.IsNullOrWhiteSpace(label) || label.Length > 255 || table.Columns.Where((_, i) => i != index).Contains(label, StringComparer.OrdinalIgnoreCase))
            { message = "Table headers must be nonempty and unique"; return false; }
        }
        return true;
    }

    private void UpdateTableHeaders(int row)
    {
        for (int i=0; i<_tables.Count; ++i)
        {
            var table = _tables[i];
            if (table.Range.TopLeft.Row != row) continue;
            string[] labels = Enumerable.Range(table.Range.TopLeft.Column,table.Range.ColumnCount).Select(c => Grid.Rows[row][c]?.ToString() ?? "").ToArray();
            _tables[i] = table with { Columns = Array.AsReadOnly(labels) };
        }
    }
    public IReadOnlyList<DataRowView> SortedTableView(Guid id, int column, bool descending)
    {
        TableDefinition table = _tables.Single(t => t.Id == id);
        if (column<table.Range.TopLeft.Column || column>table.Range.BottomRight.Column) throw new ArgumentOutOfRangeException(nameof(column));
        List<DataRowView> rows = Grid.DefaultView.Cast<DataRowView>().ToList();
        int start = table.Range.TopLeft.Row+1;
        int count = table.Range.RowCount-1-(table.Totals ? 1 : 0);
        var sorted = rows.Skip(start).Take(count).OrderBy(v => v, Comparer<DataRowView>.Create((a,b) => {
            int ar=Grid.Rows.IndexOf(a.Row), br=Grid.Rows.IndexOf(b.Row);
            int cmp=CompareNative(ar,br,column);
            return cmp == 0 ? ar.CompareTo(br) : descending ? -cmp : cmp;
        })).ToArray();
        for(int i=0;i<count;++i) rows[start+i]=sorted[i];
        return new SortedRows(Grid.DefaultView, rows);
    }
    // WPF needs the original DataTable descriptors to generate editable cell
    // columns; reflection over a plain List<DataRowView> exposes wrapper fields.
    private sealed class SortedRows : Collection<DataRowView>, IReadOnlyList<DataRowView>, ITypedList
    {
        private readonly ITypedList _source;
        public SortedRows(DataView source, IList<DataRowView> rows) : base(rows) { _source = source; }
        public PropertyDescriptorCollection GetItemProperties(PropertyDescriptor[]? accessors) => _source.GetItemProperties(accessors);
        public string GetListName(PropertyDescriptor[]? accessors) => _source.GetListName(accessors);
    }
    private int CompareNative(int a,int b,int column)
    {
        bool an=TryNumber(a,column,out var x),bn=TryNumber(b,column,out var y);
        if(an!=bn) return an?-1:1;
        if(an) return ((BigInteger)x.Numerator*y.Denominator).CompareTo((BigInteger)y.Numerator*x.Denominator);
        return StringComparer.Ordinal.Compare(Grid.Rows[a][column]?.ToString(),Grid.Rows[b][column]?.ToString());
    }
    private bool TryNumber(int row,int column,out KhzRational value)
    {
        value=default;
        if (_native is null || _native.TryGetCell((uint)column,(uint)row,out var cell) != SheetStatus.Ok ||
            cell.IsDirty || cell.ErrorCode != CellErrorCode.None || cell.Kind is not (CellKind.Rational or CellKind.Formula)) return false;
        value=new KhzRational {Numerator=cell.Value.Num,Denominator=cell.Value.Den}; return true;
    }
    public bool AddChart(ChartKind kind, CellRange selection, out string message)
    {
        message="chart needs labels and values in two adjacent columns";
        if(selection.EndColumn != selection.StartColumn+1 || selection.EndRow<selection.StartRow ||
            !InBounds(selection.StartRow,selection.StartColumn) || !InBounds(selection.EndRow,selection.EndColumn) || _charts.Count>=32) return false;
        CoreRange.TryParse($"{ColumnName(selection.StartColumn)}{selection.StartRow+1}:{ColumnName(selection.StartColumn)}{selection.EndRow+1}",out var categories);
        CoreRange.TryParse($"{ColumnName(selection.EndColumn)}{selection.StartRow+1}:{ColumnName(selection.EndColumn)}{selection.EndRow+1}",out var values);
        ChartDefinition chart=new(Guid.NewGuid(),$"{kind} · {Name}",kind,categories,values);
        if(!chart.IsValid) return false;
        _charts.Add(chart); _undo.Push(new ObjectEdit(null,chart)); _redo.Clear();
        message=$"{kind} chart · {values.RowCount} points"; return true;
    }
    public IReadOnlyList<ChartPoint> SampleChart(ChartDefinition chart)
    {
        if(!chart.IsValid) throw new ArgumentException("Invalid chart");
        List<ChartPoint> points=new();
        for(int i=0;i<chart.Values.RowCount;++i)
        {
            int r=chart.Values.TopLeft.Row+i;
            string label=Grid.Rows[chart.Categories.TopLeft.Row+i][chart.Categories.TopLeft.Column]?.ToString() ?? "";
            points.Add(new ChartPoint(label,TryNumber(r,chart.Values.TopLeft.Column,out var value)?value:null));
        }
        return points;
    }
    public bool LoadXlsx(string path,out string message)
    {
        // Parse and evaluate in a separate session; publish only after all parts
        // and formulas are valid. Failed import leaves this session untouched.
        using WorksheetSession candidate=new(Name);
        if (!XlsxPresentationSerializer.TryLoad(path,candidate,out message)) return false;
        _native?.Dispose(); _native=candidate._native; candidate._native=null;
        _inputs.Clear(); foreach(var item in candidate._inputs) _inputs.Add(item.Key,item.Value);
        _formats.Clear(); foreach(var item in candidate._formats) _formats.Add(item.Key,item.Value);
        _tableFormats.Clear(); _tables.Clear(); _tables.AddRange(candidate._tables);
        _charts.Clear(); _charts.AddRange(candidate._charts);
        _columnWidths.Clear(); foreach(var item in candidate._columnWidths) _columnWidths.Add(item.Key,item.Value);
        _rowHeights.Clear(); foreach(var item in candidate._rowHeights) _rowHeights.Add(item.Key,item.Value);
        Theme=candidate.Theme; FrozenColumns=candidate.FrozenColumns; EngineStatus=candidate.EngineStatus;
        foreach(DataRow row in Grid.Rows) foreach(DataColumn col in Grid.Columns) row[col]="";
        _undo.Clear(); _redo.Clear(); RefreshComputedCells(); return true;
    }
    internal bool ImportValue(int row,int column,string input,bool text,out string message)
    {
        message="invalid imported cell";
        if(!InBounds(row,column) || input.Length>8192 || !EngineAvailable || _inputs.ContainsKey(Key(row,column))) return false;
        SheetStatus status=text ? _native!.SetText((uint)column,(uint)row,input) :
            input.StartsWith('=') ? InstallFormula((uint)column,(uint)row,input,out message) : StoreLiteral((uint)column,(uint)row,input);
        if(status!=SheetStatus.Ok) {message=SheetStatusText.Name(status);return false;}
        _inputs.Add(Key(row,column), text ? "'"+input : input); return true;
    }
    internal void ImportObjects(IEnumerable<TableDefinition> tables,IEnumerable<ChartDefinition> charts)
    { _tables.AddRange(tables); _charts.AddRange(charts); }
}
