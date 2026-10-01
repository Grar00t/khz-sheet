using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.Numerics;
using System.Text;
using KHZ.Sheet.Core;

namespace KHZ.Sheet.Desktop;

public sealed class WorkbookSession : IDisposable
{
    private int _nextSheetNumber = 1;

    public WorkbookSession()
    {
        Sheets = new ObservableCollection<WorksheetSession>();
        AddSheet().MarkSaved();
    }

    public ObservableCollection<WorksheetSession> Sheets { get; }
    public bool HasUnsavedChanges => Sheets.Any(sheet => sheet.IsDirty);

    public WorksheetSession AddSheet(string? requestedName = null)
    {
        string name = string.IsNullOrWhiteSpace(requestedName)
            ? NextAvailableName()
            : MakeUniqueName(requestedName.Trim());

        WorksheetSession sheet = new(name);
        sheet.MarkDirty();
        Sheets.Add(sheet);
        return sheet;
    }

    private string NextAvailableName()
    {
        while (true)
        {
            string candidate = $"Sheet{_nextSheetNumber++}";
            if (!Sheets.Any(s => string.Equals(s.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }
    }

    private string MakeUniqueName(string requested)
    {
        requested = requested.Length > 31 ? requested[..31] : requested;
        if (!Sheets.Any(s => string.Equals(s.Name, requested, StringComparison.OrdinalIgnoreCase)))
        {
            return requested;
        }

        int suffix = 2;
        while (true)
        {
            string suffixText=$" {suffix++}";
            string candidate=requested[..Math.Min(requested.Length,31-suffixText.Length)]+suffixText;
            if(!Sheets.Any(s=>string.Equals(s.Name,candidate,StringComparison.OrdinalIgnoreCase))) return candidate;
        }
    }

    public bool TryRenameSheet(WorksheetSession sheet, string? requestedName, out string message)
    {
        string name = requestedName?.Trim() ?? string.Empty;
        if (!Sheets.Contains(sheet)) { message = "worksheet not found"; return false; }
        if (name.Length is < 1 or > 31 || name.IndexOfAny([':', '\\', '/', '?', '*', '[', ']']) >= 0)
        { message = "sheet names must be 1–31 characters and cannot contain : \\ / ? * [ ]"; return false; }
        if (Sheets.Any(other => !ReferenceEquals(other, sheet) &&
            string.Equals(other.Name, name, StringComparison.OrdinalIgnoreCase)))
        { message = "a sheet with that name already exists"; return false; }
        sheet.Rename(name);
        message = $"renamed · {name}";
        return true;
    }

    public bool RemoveSheet(WorksheetSession sheet, out string message)
    {
        if (Sheets.Count <= 1) { message = "a workbook must keep at least one sheet"; return false; }
        if (!Sheets.Remove(sheet)) { message = "worksheet not found"; return false; }
        sheet.Dispose();
        Sheets[0].MarkDirty();
        message = $"deleted · {sheet.Name}";
        return true;
    }

    public bool MoveSheet(WorksheetSession sheet, int newIndex, out string message)
    {
        int currentIndex = Sheets.IndexOf(sheet);
        if (currentIndex < 0 || newIndex < 0 || newIndex >= Sheets.Count)
        { message = "sheet order is out of range"; return false; }
        Sheets.Move(currentIndex, newIndex);
        sheet.MarkDirty();
        message = "sheet order updated";
        return true;
    }

    public WorksheetSession? DuplicateSheet(WorksheetSession source, out string message)
    {
        if (!Sheets.Contains(source)) { message = "worksheet not found"; return null; }
        string name = MakeUniqueName((source.Name.Length>26?source.Name[..26]:source.Name)+" Copy");
        WorksheetSession? copy = source.CreateCopy(name, out message);
        if (copy is not null) { copy.MarkDirty(); Sheets.Add(copy); }
        return copy;
    }

    public void Dispose()
    {
        foreach (WorksheetSession sheet in Sheets)
        {
            sheet.Dispose();
        }

        Sheets.Clear();
    }
}

public sealed partial class WorksheetSession : IDisposable
{
    private const int DefaultRows = 256;
    private const int DefaultColumns = 52;
    private static readonly nuint ArenaBytes = (nuint)(64 * 1024 * 1024);
    private static readonly nuint CellCapacity = (nuint)200_000;

    private readonly Dictionary<long, string> _inputs = new();
    private readonly Dictionary<long, CellFormat> _formats = new();
    private readonly List<TableFormat> _tableFormats = new();
    private readonly Stack<WorksheetEdit> _undo = new();
    private readonly Stack<WorksheetEdit> _redo = new();
    private NativeSheet? _native;
    private bool _disposed;

    private abstract record WorksheetEdit;
    private sealed record CellEdit(int Row, int Column, string Before, string After) : WorksheetEdit;
    private sealed record FormatEdit(IReadOnlyList<FormatChange> Changes) : WorksheetEdit;
    private sealed record TableEdit(TableFormat Table) : WorksheetEdit;
    private readonly record struct FormatChange(long Key, CellFormat? Before, CellFormat? After);

    public WorksheetSession(string name) : this(name, initializeEngine: true, loadError: null)
    {
    }

    internal WorksheetSession(string name, bool initializeEngine, string? loadError)
    {
        Name = name;
        Grid = CreateGrid(DefaultRows, DefaultColumns);

        if (!initializeEngine)
        {
            EngineStatus = SheetStatus.ErrState;
            EngineLoadError = loadError ?? "khz_sheet.dll is unavailable.";
            return;
        }

        try
        {
            EngineStatus = NativeSheet.TryCreate(ArenaBytes, CellCapacity, out _native);
            if (!EngineAvailable)
                EngineLoadError = $"Native calculation engine unavailable ({SheetStatusText.Name(EngineStatus)}). Verify khz_sheet.dll, its VC++ runtime, and process architecture.";
        }
        catch (DllNotFoundException ex)
        {
            EngineStatus = SheetStatus.ErrState;
            EngineLoadError = $"Could not load khz_sheet.dll or the required VC++ runtime: {ex.Message}";
        }
        catch (BadImageFormatException ex)
        {
            EngineStatus = SheetStatus.ErrState;
            EngineLoadError = $"khz_sheet.dll architecture does not match the desktop process: {ex.Message}";
        }
        catch (EntryPointNotFoundException ex)
        {
            EngineStatus = SheetStatus.ErrUnsupported;
            EngineLoadError = $"khz_sheet.dll ABI entry point is unavailable: {ex.Message}";
        }
    }

    public string Name { get; private set; }

    public bool IsDirty { get; private set; }
    public event EventHandler? Changed;

    internal void Rename(string name)
    {
        Name = name;
        MarkDirty();
    }

    internal void MarkDirty()
    {
        IsDirty = true;
        Changed?.Invoke(this,EventArgs.Empty);
    }

    internal void MarkSaved()
    {
        IsDirty = false;
        Changed?.Invoke(this,EventArgs.Empty);
    }

    public DataTable Grid { get; }

    public SheetStatus EngineStatus { get; private set; }

    public string? EngineLoadError { get; }

    public bool EngineAvailable => EngineStatus == SheetStatus.Ok && _native is not null;

    public nuint NativeCellCount => _native?.CellCount ?? 0;

    public ulong NativeCommits => _native?.Commits ?? 0UL;

    public string EngineSummary =>
        EngineAvailable
            ? $"native · exact · cells {NativeCellCount:N0} · commits {NativeCommits:N0}"
            : $"managed view · {SheetStatusText.Name(EngineStatus)}";

    public string GetInput(int row, int column)
    {
        if (!InBounds(row, column))
        {
            return string.Empty;
        }

        long key = Key(row, column);
        if (_inputs.TryGetValue(key, out string? input))
        {
            return input;
        }

        return string.Empty;
    }

    public bool CommitCell(int row, int column, string? input, out string message)
    {
        string before = GetInput(row, column);
        string after = input ?? string.Empty;
        if (!ValidateTableEdit(row, column, after, out message)) return false;
        bool ok = CommitCellCore(row, column, after, out message);
        if (ok) UpdateTableHeaders(row);
        if (ok && !string.Equals(before, after, StringComparison.Ordinal)) MarkDirty();
        if (ok && !string.Equals(before, after, StringComparison.Ordinal))
        {
            _undo.Push(new CellEdit(row, column, before, after));
            _redo.Clear();
        }
        return ok;
    }

    private bool CommitCellCore(int row, int column, string raw, out string message)
    {
        message = string.Empty;

        if (!InBounds(row, column))
        {
            message = SheetStatusText.Name(SheetStatus.ErrRange);
            return false;
        }

        if (!EngineAvailable)
        {
            if (RequiresCalculation(raw))
            {
                message = EngineLoadError ?? EngineSummary;
                return false;
            }

            _inputs[Key(row, column)] = raw;
            Grid.Rows[row][column] = raw;
            message = "text stored · " + EngineSummary;
            return true;
        }

        SheetStatus status;
        ulong evaluated = 0UL;

        if (raw.StartsWith("=", StringComparison.Ordinal))
        {
            status = InstallFormula((uint)column, (uint)row, raw, out string formulaError);
            if (status != SheetStatus.Ok)
            {
                message = formulaError;
                return false;
            }

            status = _native!.TryRecalculate(out evaluated);
            if (status != SheetStatus.Ok)
            {
                _inputs[Key(row,column)] = raw;
                Grid.Rows[row][column] = raw;
                message = "stored · recalculation " + SheetStatusText.Name(status);
                return true;
            }
        }
        else
        {
            status = StoreLiteral((uint)column, (uint)row, raw);
            if (status != SheetStatus.Ok)
            {
                message = SheetStatusText.Name(status);
                return false;
            }

            status = _native!.TryRecalculate(out evaluated);
            if (status != SheetStatus.Ok && status != SheetStatus.ErrMissing)
            {
                _inputs[Key(row, column)] = raw;
                RefreshComputedCells();
                message = "stored · recalculation " + SheetStatusText.Name(status);
                return true;
            }
        }

        _inputs[Key(row, column)] = raw;
        RefreshComputedCells();
        message = evaluated == 0UL
            ? EngineSummary
            : $"recalculated {evaluated:N0} · {EngineSummary}";
        return true;
    }

    public bool Undo(out string message)
    {
        if (_undo.Count == 0)
        {
            message = "nothing to undo";
            return false;
        }

        WorksheetEdit edit = _undo.Pop();
        if (!ApplyHistory(edit, undo: true, out message))
        {
            _undo.Push(edit);
            return false;
        }

        _redo.Push(edit);
        MarkDirty();
        return true;
    }

    public bool Redo(out string message)
    {
        if (_redo.Count == 0)
        {
            message = "nothing to redo";
            return false;
        }

        WorksheetEdit edit = _redo.Pop();
        if (!ApplyHistory(edit, undo: false, out message))
        {
            _redo.Push(edit);
            return false;
        }

        _undo.Push(edit);
        MarkDirty();
        return true;
    }

    public CellFormat GetEffectiveFormat(int row, int column)
    {
        if (!InBounds(row, column)) return CellFormat.Empty;

        CellFormat result = Theme is null ? CellFormat.Empty : new CellFormat(Foreground: Theme.Text, Background: Theme.Background);
        foreach (TableFormat table in _tableFormats)
        {
            if (table.Range.Contains(row, column))
                result = result.Merge(table.FormatFor(row));
        }

        foreach (TableDefinition table in Tables)
        {
            if (row < table.Range.TopLeft.Row || row > table.Range.BottomRight.Row ||
                column < table.Range.TopLeft.Column || column > table.Range.BottomRight.Column) continue;
            SheetTheme palette = Theme ?? SheetTheme.Presets[2];
            bool header = row == table.Range.TopLeft.Row;
            bool total = table.Totals && row == table.Range.BottomRight.Row;
            result = result.Merge(new CellFormat(Bold: header || total,
                Foreground: header ? (palette.Name == "High Contrast" ? "#000000" : "#FFFFFF") : palette.Text,
                Background: header ? palette.Accent : ((row - table.Range.TopLeft.Row) % 2 == 1 ? palette.Band : palette.Background),
                Border: palette.Grid, BorderThickness: 1));
        }
        if (_formats.TryGetValue(Key(row, column), out CellFormat? explicitFormat))
            result = result.Merge(explicitFormat);
        return result;
    }

    public bool ApplyFormat(IEnumerable<(int Row, int Column)> cells, CellFormat overlay, out string message)
    {
        if (!overlay.IsValid) { message = "invalid cell format"; return false; }
        List<FormatChange> changes = new();
        foreach ((int row, int column) in cells.Distinct())
        {
            if (!InBounds(row, column)) continue;
            long key = Key(row, column);
            _formats.TryGetValue(key, out CellFormat? before);
            CellFormat after = (before ?? CellFormat.Empty).Merge(overlay);
            if (Equals(before, after)) continue;
            _formats[key] = after;
            changes.Add(new FormatChange(key, before, after));
        }

        if (changes.Count == 0) { message = "format unchanged"; return false; }
        _undo.Push(new FormatEdit(changes));
        _redo.Clear();
        MarkDirty();
        message = $"formatted {changes.Count:N0} cells";
        return true;
    }

    public bool ClearFormat(IEnumerable<(int Row, int Column)> cells, out string message)
    {
        List<FormatChange> changes = new();
        foreach ((int row, int column) in cells.Distinct())
        {
            long key = Key(row, column);
            if (!_formats.Remove(key, out CellFormat? before)) continue;
            changes.Add(new FormatChange(key, before, null));
        }
        if (changes.Count == 0) { message = "no explicit formatting"; return false; }
        _undo.Push(new FormatEdit(changes));
        _redo.Clear();
        MarkDirty();
        message = $"cleared format · {changes.Count:N0} cells";
        return true;
    }

    public bool ApplyTableFormat(CellRange range, out string message)
    {
        if (!InBounds(range.StartRow, range.StartColumn) || !InBounds(range.EndRow, range.EndColumn))
        { message = SheetStatusText.Name(SheetStatus.ErrRange); return false; }
        TableFormat table = new(Guid.NewGuid(), range);
        _tableFormats.Add(table);
        _undo.Push(new TableEdit(table));
        _redo.Clear();
        MarkDirty();
        message = $"table style · {ColumnName(range.StartColumn)}{range.StartRow + 1}:{ColumnName(range.EndColumn)}{range.EndRow + 1}";
        return true;
    }

    internal IReadOnlyList<CellPresentation> CapturePresentationCells()
    {
        HashSet<long> keys = new(_formats.Keys);
        if (Theme is not null) keys.UnionWith(_inputs.Keys);
        foreach (TableDefinition table in Tables)
            for (int r = table.Range.TopLeft.Row; r <= table.Range.BottomRight.Row; ++r)
            for (int c = table.Range.TopLeft.Column; c <= table.Range.BottomRight.Column; ++c) keys.Add(Key(r,c));
        foreach (TableFormat table in _tableFormats)
        {
            for (int row = table.Range.StartRow; row <= table.Range.EndRow; row++)
            for (int column = table.Range.StartColumn; column <= table.Range.EndColumn; column++)
                keys.Add(Key(row, column));
        }

        List<CellPresentation> cells = new(keys.Count);
        foreach (long key in keys.OrderBy(x => x))
        {
            DecodeKey(key, out int row, out int column);
            CellFormat format = GetEffectiveFormat(row, column);
            if (!format.IsEmpty) cells.Add(new CellPresentation(row, column, format));
        }
        return cells;
    }

    public bool TryFindInput(string query, int startRow, int startColumn, out int row, out int column)
    {
        row = -1;
        column = -1;
        if (string.IsNullOrEmpty(query)) return false;

        int columns = Grid.Columns.Count;
        int total = Grid.Rows.Count * columns;
        int start = startRow >= 0 && startColumn >= 0
            ? ((startRow * columns + startColumn + 1) % total)
            : 0;

        for (int offset = 0; offset < total; offset++)
        {
            int index = (start + offset) % total;
            int r = index / columns;
            int c = index % columns;
            if (GetInput(r, c).Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                row = r;
                column = c;
                return true;
            }
        }
        return false;
    }

    private bool ApplyHistory(WorksheetEdit edit, bool undo, out string message)
    {
        switch (edit)
        {
            case CellEdit cell:
            {
                string value = undo ? cell.Before : cell.After;
                if (!CommitCellCore(cell.Row, cell.Column, value, out message)) return false;
                UpdateTableHeaders(cell.Row);
                message = $"{(undo ? "undo" : "redo")} · {ColumnName(cell.Column)}{cell.Row + 1}";
                return true;
            }
            case FormatEdit format:
                foreach (FormatChange change in format.Changes)
                {
                    CellFormat? value = undo ? change.Before : change.After;
                    if (value is null) _formats.Remove(change.Key);
                    else _formats[change.Key] = value;
                }
                message = $"{(undo ? "undo" : "redo")} format · {format.Changes.Count:N0} cells";
                return true;
            case TableEdit table:
                if (undo) _tableFormats.RemoveAll(x => x.Id == table.Table.Id);
                else if (!_tableFormats.Any(x => x.Id == table.Table.Id)) _tableFormats.Add(table.Table);
                message = $"{(undo ? "undo" : "redo")} table style";
                return true;
            case ObjectEdit obj:
                return ApplyObjectHistory(obj, undo, out message);
            case TableTotalsEdit totals:
                return ApplyTotalsHistory(totals, undo, out message);
            default:
                message = "unknown history entry";
                return false;
        }
    }

    public bool Recalculate(out string message)
    {
        if (!EngineAvailable)
        {
            message = EngineSummary;
            return false;
        }

        SheetStatus status = _native!.TryRecalculate(out ulong evaluated);
        if (status != SheetStatus.Ok)
        {
            message = SheetStatusText.Name(status);
            return false;
        }

        RefreshComputedCells();
        message = $"recalculated {evaluated:N0} · {EngineSummary}";
        return true;
    }

    public bool Verify(out string message)
    {
        if (!EngineAvailable)
        {
            message = EngineSummary;
            return false;
        }

        SheetStatus status = _native!.VerifyChain(out nuint failedIndex);
        if (status != SheetStatus.Ok)
        {
            message = $"{SheetStatusText.Name(status)} · link {failedIndex}";
            return false;
        }

        status = _native.TryProofHex(out string proof);
        if (status != SheetStatus.Ok)
        {
            message = SheetStatusText.Name(status);
            return false;
        }

        string head = proof.Length > 16 ? proof[..16] : proof;
        message = $"proof verified · {head} · commits {NativeCommits:N0}";
        return true;
    }

    public bool TryGetProof(out string proof, out string message)
    {
        proof = string.Empty;
        if (!Verify(out message))
        {
            return false;
        }

        SheetStatus status = _native!.TryProofHex(out proof);
        if (status != SheetStatus.Ok)
        {
            proof = string.Empty;
            message = SheetStatusText.Name(status);
            return false;
        }

        return true;
    }

    public bool ExportXlsx(string path, out string message)
    {
        message = string.Empty;
        if (!EngineAvailable)
        {
            message = EngineSummary;
            return false;
        }

        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            message = ex.Message;
            return false;
        }

        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            message = SheetStatusText.Name(SheetStatus.ErrRange);
            return false;
        }

        string fileName = Path.GetFileName(fullPath);
        string token = Guid.NewGuid().ToString("N");
        string nativeTemp = Path.Combine(directory, $".{fileName}.{token}.native.tmp");
        string styledTemp = Path.Combine(directory, $".{fileName}.{token}.styled.tmp");

        try
        {
            SheetStatus status = _native!.TryWriteXlsx(nativeTemp, Name, out XlsxReport report);
            if (status != SheetStatus.Ok)
            {
                message = SheetStatusText.Name(status);
                return false;
            }

            IReadOnlyList<CellPresentation> presentation = CapturePresentationCells();
            string candidate = nativeTemp;
            string styleSummary = string.Empty;
            if (presentation.Count > 0 || HasWorkbookObjects)
            {
                if (!XlsxPresentationSerializer.TryApply(
                    nativeTemp, styledTemp, presentation, this, out styleSummary))
                {
                    message = $"xlsx style export failed · {styleSummary}";
                    return false;
                }
                candidate = styledTemp;
            }

            File.Move(candidate, fullPath, overwrite: true);
            long finalBytes = new FileInfo(fullPath).Length;
            string styled = string.IsNullOrEmpty(styleSummary) ? string.Empty : $" · {styleSummary}";
            message = $"xlsx · {report.CellsWritten:N0} cells · {finalBytes:N0} bytes · lossy {report.LossyCells:N0}{styled}";
            return true;
        }
        catch (DllNotFoundException)
        {
            message = SheetStatusText.Name(SheetStatus.ErrState);
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            message = SheetStatusText.Name(SheetStatus.ErrUnsupported);
            return false;
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
        finally
        {
            TryDelete(nativeTemp);
            TryDelete(styledTemp);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void SaveCsv(string path)
    {
        using StreamWriter writer = new(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        int lastRow = LastPopulatedRow();
        int lastColumn = LastPopulatedColumn();

        if (lastRow < 0 || lastColumn < 0)
        {
            return;
        }

        for (int row = 0; row <= lastRow; row++)
        {
            for (int column = 0; column <= lastColumn; column++)
            {
                if (column > 0)
                {
                    writer.Write(',');
                }

                writer.Write(EscapeCsv(GetInput(row, column)));
            }

            writer.WriteLine();
        }
    }

    public bool LoadCsv(string path, out string message)
    {
        message = string.Empty;
        string text = File.ReadAllText(path);
        List<List<string>> records;

        try
        {
            records = ParseCsv(text);
        }
        catch (FormatException ex)
        {
            message = ex.Message;
            return false;
        }

        int rowCount = Math.Min(records.Count, Grid.Rows.Count);
        int imported = 0;

        if(!EngineAvailable)
        {
            for(int row=0;row<rowCount;++row)
            for(int column=0;column<Math.Min(records[row].Count,Grid.Columns.Count);++column)
                if(records[row][column].Length!=0 && RequiresCalculation(records[row][column]))
                {
                    message=$"{EngineLoadError??EngineSummary} · CSV cell {ColumnName(column)}{row+1} requires the native engine";
                    return false;
                }
        }

        WorksheetSession? candidate;
        if(EngineAvailable)
            candidate=CreateCopy(Name,out message);
        else
        {
            candidate=new WorksheetSession(Name,false,EngineLoadError);
            foreach(var pair in _inputs)
            {
                DecodeKey(pair.Key,out int oldRow,out int oldColumn);
                if(RequiresCalculation(pair.Value))
                {
                    message=EngineLoadError??EngineSummary;
                    candidate.Dispose();
                    return false;
                }
                if(!candidate.CommitCell(oldRow,oldColumn,pair.Value,out message))
                { candidate.Dispose(); return false; }
            }
            foreach(var item in _formats) candidate._formats.Add(item.Key,item.Value);
            candidate._tableFormats.AddRange(_tableFormats);
            candidate._tables.AddRange(_tables);
            candidate._charts.AddRange(_charts);
            foreach(var item in _columnWidths) candidate._columnWidths.Add(item.Key,item.Value);
            foreach(var item in _rowHeights) candidate._rowHeights.Add(item.Key,item.Value);
            candidate.Theme=Theme;
            candidate.FrozenColumns=FrozenColumns;
        }

        if(candidate is null) return false;
        using(candidate)
        {
            for (int row = 0; row < rowCount; row++)
            {
                int columnCount = Math.Min(records[row].Count, Grid.Columns.Count);
                for (int column = 0; column < columnCount; column++)
                {
                    string value = records[row][column];
                    if (value.Length == 0) continue;
                    if(!candidate.CommitCell(row,column,value,out string cellError))
                    {
                        message=$"CSV import rejected at {ColumnName(column)}{row+1} · {cellError}";
                        return false;
                    }
                    imported++;
                }
            }
            if(candidate.EngineAvailable && !candidate.Recalculate(out message)) return false;
            AdoptCsvCandidate(candidate);
        }
        message = $"csv · {imported:N0} populated cells";
        MarkSaved();
        return true;
    }

    private void AdoptCsvCandidate(WorksheetSession candidate)
    {
        _native?.Dispose();
        _native=candidate._native;
        candidate._native=null;
        _inputs.Clear();
        foreach(var item in candidate._inputs) _inputs.Add(item.Key,item.Value);
        _formats.Clear();
        foreach(var item in candidate._formats) _formats.Add(item.Key,item.Value);
        _tableFormats.Clear(); _tableFormats.AddRange(candidate._tableFormats);
        _tables.Clear(); _tables.AddRange(candidate._tables);
        _charts.Clear(); _charts.AddRange(candidate._charts);
        _columnWidths.Clear();
        foreach(var item in candidate._columnWidths) _columnWidths.Add(item.Key,item.Value);
        _rowHeights.Clear();
        foreach(var item in candidate._rowHeights) _rowHeights.Add(item.Key,item.Value);
        Theme=candidate.Theme;
        FrozenColumns=candidate.FrozenColumns;
        EngineStatus=candidate.EngineStatus;
        _undo.Clear(); _redo.Clear();
        for(int row=0;row<Grid.Rows.Count;++row)
            for(int column=0;column<Grid.Columns.Count;++column)
                Grid.Rows[row][column]=candidate.Grid.Rows[row][column];
        RefreshComputedCells();
    }

    internal string RenderInput(int row, int column) => RenderCell(row, column, GetInput(row, column));
    public event EventHandler? ValuesChanged;

    public void RefreshComputedCells()
    {
        if (!EngineAvailable)
        {
            return;
        }

        foreach (KeyValuePair<long, string> pair in _inputs)
        {
            DecodeKey(pair.Key, out int row, out int column);
            Grid.Rows[row][column] = RenderCell(row, column, pair.Value);
        }
        ValuesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _native?.Dispose();
        _native = null;
        _disposed = true;
    }

    public static string ColumnName(int zeroBasedColumn)
    {
        if (zeroBasedColumn < 0)
        {
            return string.Empty;
        }

        StringBuilder builder = new();
        int value = zeroBasedColumn + 1;

        while (value > 0)
        {
            value--;
            builder.Insert(0, (char)('A' + (value % 26)));
            value /= 26;
        }

        return builder.ToString();
    }

    private SheetStatus InstallFormula(uint column, uint row, string source, out string message)
    {
        FormulaParseFailure failure;
        SheetStatus status = _native!.TrySetFormula(column, row, source, out failure);

        if (status != SheetStatus.Ok && source.Length > 1)
        {
            status = _native.TrySetFormula(column, row, source[1..], out failure);
        }

        message = status == SheetStatus.Ok
            ? string.Empty
            : $"{SheetStatusText.Name(status)} · byte {failure.Offset} · {failure.Expected}";

        return status;
    }

    private SheetStatus StoreLiteral(uint column, uint row, string raw)
    {
        if (raw.StartsWith('\'')) return _native!.SetText(column, row, raw[1..]);
        string value = raw.Trim();

        if (value.Length == 0)
        {
            return _native!.SetText(column, row, ReadOnlySpan<byte>.Empty);
        }

        if (string.Equals(value, "TRUE", StringComparison.OrdinalIgnoreCase))
        {
            return _native!.SetBool(column, row, true);
        }

        if (string.Equals(value, "FALSE", StringComparison.OrdinalIgnoreCase))
        {
            return _native!.SetBool(column, row, false);
        }

        if (TryCellError(value, out CellErrorCode error))
        {
            return _native!.SetError(column, row, error);
        }

        SheetStatus numericStatus = TryParseExactNumber(value, out long numerator, out long denominator, out bool numeric);
        if (numeric)
        {
            if (numericStatus != SheetStatus.Ok)
            {
                return numericStatus;
            }

            return denominator == 1L
                ? _native!.SetInt64(column, row, numerator)
                : _native!.SetRational(column, row, numerator, denominator);
        }

        return _native!.SetText(column, row, raw);
    }

    private string RenderCell(int row, int column, string fallback)
    {
        SheetStatus status = _native!.TryGetCell((uint)column, (uint)row, out KhzCellNative cell);
        if (status != SheetStatus.Ok)
        {
            return fallback;
        }

        if (cell.ErrorCode != CellErrorCode.None)
        {
            return ErrorText(cell.ErrorCode);
        }

        switch (cell.Kind)
        {
            case CellKind.Empty:
                return string.Empty;

            case CellKind.Rational:
            case CellKind.Formula:
                return cell.Value.ToString();

            case CellKind.Bool:
                return cell.BoolValue != 0u ? "TRUE" : "FALSE";

            case CellKind.Error:
                return ErrorText(cell.ErrorCode);

            case CellKind.Text:
            {
                status = _native.TryGetTextBytes((uint)column, (uint)row, out ReadOnlySpan<byte> utf8);
                return status == SheetStatus.Ok ? Encoding.UTF8.GetString(utf8) : fallback;
            }

            default:
                return fallback;
        }
    }

    private static DataTable CreateGrid(int rows, int columns)
    {
        DataTable table = new("KHZ Sheet");

        for (int column = 0; column < columns; column++)
        {
            table.Columns.Add(ColumnName(column), typeof(string));
        }

        for (int row = 0; row < rows; row++)
        {
            DataRow created = table.NewRow();
            for (int column = 0; column < columns; column++)
            {
                created[column] = string.Empty;
            }

            table.Rows.Add(created);
        }

        return table;
    }

    private bool InBounds(int row, int column) =>
        row >= 0 && row < Grid.Rows.Count && column >= 0 && column < Grid.Columns.Count;

    private int LastPopulatedRow()
    {
        int last = -1;
        foreach (long key in _inputs.Keys)
        {
            DecodeKey(key, out int row, out _);
            last = Math.Max(last, row);
        }

        return last;
    }

    private int LastPopulatedColumn()
    {
        int last = -1;
        foreach (long key in _inputs.Keys)
        {
            DecodeKey(key, out _, out int column);
            last = Math.Max(last, column);
        }

        return last;
    }

    private static long Key(int row, int column) => ((long)row << 32) | (uint)column;

    private static void DecodeKey(long key, out int row, out int column)
    {
        row = (int)(key >> 32);
        column = (int)(key & 0xFFFFFFFFL);
    }

    private static bool TryCellError(string value, out CellErrorCode error)
    {
        switch (value.ToUpperInvariant())
        {
            case "#NULL!":
                error = CellErrorCode.Null;
                return true;
            case "#DIV/0!":
                error = CellErrorCode.Div0;
                return true;
            case "#VALUE!":
                error = CellErrorCode.Value;
                return true;
            case "#REF!":
                error = CellErrorCode.Ref;
                return true;
            case "#NAME?":
                error = CellErrorCode.Name;
                return true;
            case "#NUM!":
                error = CellErrorCode.Num;
                return true;
            case "#N/A":
                error = CellErrorCode.Na;
                return true;
            default:
                error = CellErrorCode.None;
                return false;
        }
    }

    private static bool RequiresCalculation(string raw)
    {
        string value = raw.Trim();
        if (value.StartsWith('=') ||
            string.Equals(value, "TRUE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "FALSE", StringComparison.OrdinalIgnoreCase) ||
            TryCellError(value, out _))
        {
            return true;
        }

        SheetStatus status = TryParseExactNumber(value, out _, out _, out bool recognized);
        return recognized || status != SheetStatus.Ok;
    }

    private static string ErrorText(CellErrorCode error) =>
        error switch
        {
            CellErrorCode.Null => "#NULL!",
            CellErrorCode.Div0 => "#DIV/0!",
            CellErrorCode.Value => "#VALUE!",
            CellErrorCode.Ref => "#REF!",
            CellErrorCode.Name => "#NAME?",
            CellErrorCode.Num => "#NUM!",
            CellErrorCode.Na => "#N/A",
            _ => "#ERROR!"
        };

    private static SheetStatus TryParseExactNumber(
        string text,
        out long numerator,
        out long denominator,
        out bool recognized)
    {
        numerator = 0L;
        denominator = 1L;
        recognized = false;

        int slash = text.IndexOf('/');
        if (slash > 0 && slash == text.LastIndexOf('/'))
        {
            string left = text[..slash].Trim();
            string right = text[(slash + 1)..].Trim();

            if (long.TryParse(left, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) &&
                long.TryParse(right, NumberStyles.Integer, CultureInfo.InvariantCulture, out long d))
            {
                recognized = true;
                if (d == 0L)
                {
                    return SheetStatus.ErrDivZero;
                }

                BigInteger bn = n;
                BigInteger bd = d;
                Normalize(ref bn, ref bd);

                if (bn < long.MinValue || bn > long.MaxValue || bd < 1 || bd > long.MaxValue)
                {
                    return SheetStatus.ErrOverflow;
                }

                numerator = (long)bn;
                denominator = (long)bd;
                return SheetStatus.Ok;
            }

            return SheetStatus.Ok;
        }

        int i = 0;
        bool negative = false;

        if (i < text.Length && (text[i] == '+' || text[i] == '-'))
        {
            negative = text[i] == '-';
            i++;
        }

        StringBuilder digits = new();
        bool hasDigit = false;
        bool seenPoint = false;
        int fractionDigits = 0;

        while (i < text.Length)
        {
            char c = text[i];

            if (char.IsAsciiDigit(c))
            {
                digits.Append(c);
                hasDigit = true;
                if (seenPoint)
                {
                    fractionDigits++;
                }

                i++;
                continue;
            }

            if (c == '.' && !seenPoint)
            {
                seenPoint = true;
                i++;
                continue;
            }

            break;
        }

        if (!hasDigit)
        {
            return SheetStatus.Ok;
        }

        int exponent = 0;
        if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
        {
            i++;

            bool exponentNegative = false;
            if (i < text.Length && (text[i] == '+' || text[i] == '-'))
            {
                exponentNegative = text[i] == '-';
                i++;
            }

            int start = i;
            while (i < text.Length && char.IsAsciiDigit(text[i]))
            {
                if (exponent > 10000)
                {
                    recognized = true;
                    return SheetStatus.ErrOverflow;
                }

                exponent = checked((exponent * 10) + (text[i] - '0'));
                i++;
            }

            if (i == start)
            {
                return SheetStatus.Ok;
            }

            if (exponentNegative)
            {
                exponent = -exponent;
            }
        }

        if (i != text.Length)
        {
            return SheetStatus.Ok;
        }

        recognized = true;

        if (Math.Abs((long)exponent - fractionDigits) > 256L)
        {
            return SheetStatus.ErrOverflow;
        }

        BigInteger parsed = BigInteger.Parse(digits.ToString(), CultureInfo.InvariantCulture);
        if (negative)
        {
            parsed = -parsed;
        }

        int scale = fractionDigits - exponent;
        BigInteger den = BigInteger.One;

        if (scale > 0)
        {
            den = BigInteger.Pow(10, scale);
        }
        else if (scale < 0)
        {
            parsed *= BigInteger.Pow(10, -scale);
        }

        Normalize(ref parsed, ref den);

        if (parsed < long.MinValue || parsed > long.MaxValue || den < 1 || den > long.MaxValue)
        {
            return SheetStatus.ErrOverflow;
        }

        numerator = (long)parsed;
        denominator = (long)den;
        return SheetStatus.Ok;
    }

    private static void Normalize(ref BigInteger numerator, ref BigInteger denominator)
    {
        if (denominator.Sign < 0)
        {
            numerator = -numerator;
            denominator = -denominator;
        }

        BigInteger gcd = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
        if (gcd > BigInteger.One)
        {
            numerator /= gcd;
            denominator /= gcd;
        }
    }

    private static string EscapeCsv(string value)
    {
        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static List<List<string>> ParseCsv(string text)
    {
        List<List<string>> rows = new();
        List<string> row = new();
        StringBuilder field = new();

        bool quoted = false;
        int i = 0;

        while (i < text.Length)
        {
            char c = text[i];

            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                        continue;
                    }

                    quoted = false;
                    i++;
                    continue;
                }

                field.Append(c);
                i++;
                continue;
            }

            if (c == '"' && field.Length == 0)
            {
                quoted = true;
                i++;
                continue;
            }

            if (c == ',')
            {
                row.Add(field.ToString());
                field.Clear();
                i++;
                continue;
            }

            if (c == '\r' || c == '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = new List<string>();

                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i += 2;
                }
                else
                {
                    i++;
                }

                continue;
            }

            field.Append(c);
            i++;
        }

        if (quoted)
        {
            throw new FormatException("CSV contains an unterminated quoted field.");
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }
}
