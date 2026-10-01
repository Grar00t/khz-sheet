using System.Data;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using KHZ.Sheet.Core;
using CellRange = KHZ.Sheet.Desktop.CellRange;

namespace KHZ.Sheet.Desktop;

public partial class MainWindow : Window
{
    private readonly WorkbookSession _workbook = new();
    private readonly DispatcherTimer _headerSelectionTimer = new() { Interval = TimeSpan.FromMilliseconds(35) };
    private readonly DispatcherTimer _recoveryDebounceTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _recoveryTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool _rebinding;
    private bool _formatControlReady;
    private string? _editError;
    private sealed record RecoveryEntry(string Name,string FileName);
    private sealed record RecoveryManifest(int Version,string Generation,RecoveryEntry[] Sheets);

    private sealed record ColorChoice(string Name, string? Hex);
    private sealed record NumberFormatChoice(string Name, CellNumberFormat Format);

    public MainWindow()
    {
        InitializeComponent();
        _workbook.Sheets.CollectionChanged += WorkbookSheets_CollectionChanged;
        foreach(WorksheetSession existingSheet in _workbook.Sheets) existingSheet.Changed+=Worksheet_Changed;
        _recoveryDebounceTimer.Tick += (_, _) =>
        {
            _recoveryDebounceTimer.Stop();
            SaveRecoverySnapshot();
        };
        _headerSelectionTimer.Tick += (_, _) =>
        {
            _headerSelectionTimer.Stop();
            UpdateHeaderSelection();
            RefreshSelectionStats();
        };
        SheetGrid.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(SheetGrid_ColumnResizeCompleted));
        SheetGrid.AddHandler(Control.MouseDoubleClickEvent, new MouseButtonEventHandler(SheetGrid_ColumnAutoFit), true);
        _recoveryTimer.Tick += (_, _) => SaveRecoverySnapshot();
        _recoveryTimer.Start();
        InitializeFormattingControls();
        ThemeBox.ItemsSource = SheetTheme.Presets; ThemeBox.DisplayMemberPath = nameof(SheetTheme.Name);
        ChartKindBox.ItemsSource = Enum.GetValues<ChartKind>(); ChartKindBox.SelectedIndex = 1;

        SheetList.ItemsSource = _workbook.Sheets;
        SheetList.SelectedIndex = 0;

        if (SheetList.SelectedItem is WorksheetSession sheet)
        {
            BindSheet(sheet);
        }

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }

    private WorksheetSession? CurrentSheet => SheetList.SelectedItem as WorksheetSession;

    private void InitializeFormattingControls()
    {
        FontFamilyBox.ItemsSource = Fonts.SystemFontFamilies
            .Select(x => x.Source)
            .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        FontFamilyBox.SelectedItem = CellFormatDefaults.FontFamily;
        FontSizeBox.ItemsSource = new double[] { 8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48, 72 };
        FontSizeBox.SelectedItem = CellFormatDefaults.FontSize;

        NumberFormatChoice[] numberFormats =
        [
            new("General", CellNumberFormat.General),
            new("a/b", CellNumberFormat.Fraction),
            new("USD", CellNumberFormat.Currency),
            new("ISO date", CellNumberFormat.IsoDate),
            new("0", CellNumberFormat.Integer),
            new("0.00", CellNumberFormat.Decimal2),
            new("#,##0", CellNumberFormat.Thousands),
            new("#,##0.00", CellNumberFormat.Thousands2),
            new("0%", CellNumberFormat.Percent),
            new("0.00%", CellNumberFormat.Percent2)
        ];
        NumberFormatBox.ItemsSource = numberFormats;
        NumberFormatBox.DisplayMemberPath = nameof(NumberFormatChoice.Name);
        NumberFormatBox.SelectedIndex = 0;

        ColorChoice[] textColors =
        [
            new("Text · default", null), new("White", "#FFFFFF"), new("Black", "#000000"),
            new("Green", "#3FB950"), new("Blue", "#58A6FF"), new("Red", "#FF7B72"),
            new("Orange", "#D29922"), new("Purple", "#BC8CFF")
        ];
        ColorChoice[] fills =
        [
            new("Fill · none", null), new("Slate", "#1F2630"), new("Blue", "#163A5F"),
            new("Green", "#123C2B"), new("Red", "#4A1F24"), new("Gold", "#493B12"),
            new("Purple", "#36244A"), new("White", "#FFFFFF")
        ];
        TextColorBox.ItemsSource = textColors; TextColorBox.DisplayMemberPath = nameof(ColorChoice.Name); TextColorBox.SelectedIndex = 0;
        FillColorBox.ItemsSource = fills; FillColorBox.DisplayMemberPath = nameof(ColorChoice.Name); FillColorBox.SelectedIndex = 0;
        _formatControlReady = true;
    }

    private void BindSheet(WorksheetSession sheet)
    {
        _rebinding = true;
        try
        {
            _editError = null;
            SheetGrid.AutoGenerateColumns = true;
            SheetGrid.ItemsSource = sheet.Grid.DefaultView;
            SheetGrid.FrozenColumnCount = sheet.FrozenColumns;
            ThemeBox.SelectedItem = sheet.Theme;
            ApplyThemeResources(sheet.Theme);
            SheetGrid.SelectedCells.Clear();
            SheetGrid.CurrentCell = new DataGridCellInfo();
            NameBox.Text = "A1";
            FormulaBox.Text = string.Empty;
            SetStatus($"sheet · {sheet.Name}");
            RefreshEngineText();
            UpdateErrorBanner();
        }
        finally
        {
            _rebinding = false;
        }
    }

    private void NewSheet_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmContinueWithUnsavedChanges("create another worksheet")) return;
        WorksheetSession sheet = _workbook.AddSheet();
        SheetList.SelectedItem = sheet;
        SetStatus($"created · {sheet.Name}");
    }

    private void OpenCsv_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmContinueWithUnsavedChanges("open a CSV worksheet")) return;
        OpenFileDialog dialog = new()
        {
            Title = "Open CSV",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        WorksheetSession sheet = _workbook.AddSheet(Path.GetFileNameWithoutExtension(dialog.FileName));
        SheetList.SelectedItem = sheet;

        try
        {
            if (sheet.LoadCsv(dialog.FileName, out string message))
            {
                sheet.MarkDirty();
                SetStatus(message);
                RefreshEngineText();
            }
            else
            {
                ShowEditError(message);
                SetStatus(message);
            }
        }
        catch (IOException ex)
        {
            SetStatus(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            SetStatus(ex.Message);
        }
    }

    private void SaveCsv_Click(object sender, RoutedEventArgs e)
    {
        WorksheetSession? sheet = CurrentSheet;
        if (sheet is null)
        {
            return;
        }

        SaveFileDialog dialog = new()
        {
            Title = "Save CSV",
            Filter = "CSV files (*.csv)|*.csv",
            AddExtension = true,
            DefaultExt = ".csv",
            FileName = $"{SafeFileName(sheet.Name)}.csv"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            sheet.SaveCsv(dialog.FileName);
            if(sheet.CsvCapturesEntireSheet) { sheet.MarkSaved(); SetStatus($"saved · {dialog.FileName}"); }
            else SetStatus($"saved cell values only · presentation remains unsaved · {dialog.FileName}");
        }
        catch (IOException ex)
        {
            SetStatus(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            SetStatus(ex.Message);
        }
    }

    private void ExportXlsx_Click(object sender, RoutedEventArgs e)
    {
        WorksheetSession? sheet = CurrentSheet;
        if (sheet is null)
        {
            return;
        }

        bool otherSheets = _workbook.Sheets.Count > 1;
        if (otherSheets)
        {
            if(MessageBox.Show(this,"Only the active sheet will be saved in this XLSX. Other tabs will not be included. Continue?",
                "Single-sheet export",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes) return;
            SetStatus("Only the active sheet will be exported; other tabs are not included.");
        }

        SaveFileDialog dialog = new()
        {
            Title = "Export XLSX",
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            AddExtension = true,
            DefaultExt = ".xlsx",
            FileName = $"{SafeFileName(sheet.Name)}.xlsx"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (sheet.ExportXlsx(dialog.FileName, out string message))
        {
            sheet.MarkSaved();
            SetStatus(otherSheets ? message + " · only active sheet exported" : message);
        }
        else
        {
            SetStatus($"export failed · {message}");
        }

        RefreshEngineText();
    }

    private void Recalculate_Click(object sender, RoutedEventArgs e)
    {
        WorksheetSession? sheet = CurrentSheet;
        if (sheet is null)
        {
            return;
        }

        bool ok = sheet.Recalculate(out string message);
        SetStatus(message);
        if (ok) ClearEditError(); else ShowEditError(message);
        RefreshEngineText();
    }

    private void Verify_Click(object sender, RoutedEventArgs e)
    {
        WorksheetSession? sheet = CurrentSheet;
        if (sheet is null)
        {
            return;
        }

        bool ok = sheet.Verify(out string message);
        SetStatus(message);
        if (ok) ClearEditError(); else ShowEditError(message);
        RefreshEngineText();
    }

    private void CopyProof_Click(object sender, RoutedEventArgs e)
    {
        WorksheetSession? sheet = CurrentSheet;
        if (sheet is null)
        {
            SetStatus("proof unavailable");
            return;
        }

        if (!sheet.TryGetProof(out string proof, out string message))
        {
            SetStatus(message);
            return;
        }

        try
        {
            Clipboard.SetText(proof);
            SetStatus($"proof copied · {proof[..Math.Min(16, proof.Length)]}");
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            SetStatus($"clipboard unavailable · {ex.Message}");
        }
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => UndoCurrentSheet();

    private void Redo_Click(object sender, RoutedEventArgs e) => RedoCurrentSheet();

    private void FreezeFirstColumn_Click(object sender, RoutedEventArgs e)
    {
        if (SheetGrid.Columns.Count == 0) return;
        SheetGrid.FrozenColumnCount = SheetGrid.FrozenColumnCount == 0 ? 1 : 0;
        CurrentSheet?.SetFrozenColumns(SheetGrid.FrozenColumnCount);
        RefreshRealizedCellStyles();
        SetStatus(SheetGrid.FrozenColumnCount == 0 ? "column A unfrozen" : "column A frozen");
    }

    private void FindNext_Click(object sender, RoutedEventArgs e) => FindNextInput();

    private void FontFamilyBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_formatControlReady && FontFamilyBox.SelectedItem is string font)
            ApplySelectedFormat(new CellFormat(FontFamily: font));
    }

    private void FontSizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_formatControlReady && FontSizeBox.SelectedItem is double size)
            ApplySelectedFormat(new CellFormat(FontSize: size));
    }

    private void TextColorBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_formatControlReady && TextColorBox.SelectedItem is ColorChoice { Hex: not null } color)
            ApplySelectedFormat(new CellFormat(Foreground: color.Hex));
    }

    private void FillColorBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_formatControlReady && FillColorBox.SelectedItem is ColorChoice { Hex: not null } color)
            ApplySelectedFormat(new CellFormat(Background: color.Hex));
    }

    private void Bold_Click(object sender, RoutedEventArgs e) => ApplySelectedFormat(new CellFormat(Bold: true));
    private void Italic_Click(object sender, RoutedEventArgs e) => ApplySelectedFormat(new CellFormat(Italic: true));
    private void Underline_Click(object sender, RoutedEventArgs e) => ApplySelectedFormat(new CellFormat(Underline: true));
    private void Wrap_Click(object sender, RoutedEventArgs e) => ApplySelectedFormat(new CellFormat(WrapText: true));

    private void NumberFormatBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_formatControlReady && NumberFormatBox.SelectedItem is NumberFormatChoice choice)
            ApplySelectedFormat(new CellFormat(NumberFormat: choice.Format));
    }

    private void AlignLeft_Click(object sender, RoutedEventArgs e) => ApplySelectedFormat(new CellFormat(Alignment: CellTextAlignment.Left));
    private void AlignCenter_Click(object sender, RoutedEventArgs e) => ApplySelectedFormat(new CellFormat(Alignment: CellTextAlignment.Center));
    private void AlignRight_Click(object sender, RoutedEventArgs e) => ApplySelectedFormat(new CellFormat(Alignment: CellTextAlignment.Right));
    private void Borders_Click(object sender, RoutedEventArgs e) => ApplySelectedFormat(new CellFormat(Border: "#59636E", BorderThickness: 1.0));

    private void ApplyFormula_Click(object sender, RoutedEventArgs e)
    {
        CommitFormulaBar();
    }

    private void FormulaBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        CommitFormulaBar();
    }

    private void CommitFormulaBar()
    {
        WorksheetSession? sheet = CurrentSheet;
        if (sheet is null || !TryCurrentCoordinate(out int row, out int column))
        {
            return;
        }

        if (sheet.CommitCell(row, column, FormulaBox.Text, out string message))
        {
            ClearEditError();
            SetStatus(message);
        }
        else
        {
            ShowEditError($"cell {CellName(row, column)} · {message}");
            SetStatus($"cell {CellName(row, column)} · {message}");
        }

        RefreshEngineText();
        SheetGrid.Items.Refresh();
    }

    private void SheetGrid_LoadingRow(object sender, DataGridRowEventArgs e)
    {
        int index = CurrentSheet is not null && e.Row.Item is DataRowView v ? CurrentSheet.Grid.Rows.IndexOf(v.Row) : e.Row.GetIndex();
        e.Row.Header = (index + 1).ToString();
        e.Row.Height = CurrentSheet?.RowHeights.TryGetValue(index,out double height)==true ? height*96/72 : double.NaN;
        e.Row.Loaded -= SheetGrid_RowLoaded;
        e.Row.Loaded += SheetGrid_RowLoaded;
        QueueHeaderSelectionUpdate();
    }

    private void SheetGrid_RowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DataGridRow row) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            foreach (DataGridCell cell in FindVisualChildren<DataGridCell>(row))
                ApplyCellVisualFormat(cell);
        }));
    }

    private void SheetGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        int column = CurrentSheet?.Grid.Columns.IndexOf(e.PropertyName) ?? -1;
        e.Column.Width = new DataGridLength(CurrentSheet?.ColumnWidths.TryGetValue(column,out double width)==true ? width*7+5 : 110);
        e.Column.CanUserSort = false;
        e.Column.CanUserReorder = false;
    }

    private void SheetGrid_SelectedCellsChanged(object sender, SelectedCellsChangedEventArgs e)
    {
        if (_rebinding)
        {
            return;
        }

        UpdateFormulaBar();
        RefreshRealizedCellStyles();
        QueueHeaderSelectionUpdate();
    }

    private void UpdateFormulaBar()
    {
        WorksheetSession? sheet = CurrentSheet;
        if (sheet is null || !TryCurrentCoordinate(out int row, out int column))
        {
            return;
        }

        NameBox.Text = CellName(row, column);
        FormulaBox.Text = sheet.GetInput(row, column);
        RefreshEngineText();
    }

    private void SheetGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit ||
            e.EditingElement is not TextBox editor ||
            CurrentSheet is not WorksheetSession sheet)
        {
            return;
        }

        int row = e.Row.Item is DataRowView view ? sheet.Grid.Rows.IndexOf(view.Row) : -1;
        if (row < 0) return;
        int column = e.Column.DisplayIndex;
        string value = editor.Text;

        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                if (sheet.CommitCell(row, column, value, out string message))
                {
                    ClearEditError();
                    SetStatus(message);
                }
                else
                {
                    sheet.Grid.Rows[row][column] = sheet.RenderInput(row, column);
                    string error = $"cell {CellName(row, column)} · {message}";
                    ShowEditError(error);
                    SetStatus(error);
                }

                FormulaBox.Text = sheet.GetInput(row, column);
                RefreshEngineText();
                RefreshRealizedCellStyles();
            }));
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            e.Handled = true;
            FindBox.Focus();
            FindBox.SelectAll();
            return;
        }

        if (Keyboard.FocusedElement is TextBox)
        {
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z)
        {
            e.Handled = true;
            UndoCurrentSheet();
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y)
        {
            e.Handled = true;
            RedoCurrentSheet();
        }
    }

    private void SheetGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (Keyboard.FocusedElement is TextBox && e.Key is not (Key.Enter or Key.Tab)) return;

        GridShortcut shortcut=GetGridShortcut(e.Key,Keyboard.Modifiers);
        if (shortcut==GridShortcut.Copy)
        {
            e.Handled = true;
            CopySelection();
            return;
        }

        if (shortcut==GridShortcut.Paste)
        {
            e.Handled = true;
            PasteClipboard();
            return;
        }

        if (shortcut is GridShortcut.FillDown or GridShortcut.FillRight)
        {
            e.Handled = true;
            FillSelection(down: shortcut==GridShortcut.FillDown);
            return;
        }

        if (e.Key == Key.F2)
        {
            e.Handled = true;
            if (SheetGrid.BeginEdit())
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    if (TryCurrentCoordinate(out int row, out int column) &&
                        SheetGrid.CurrentItem is DataRowView item &&
                        SheetGrid.Columns.Count > column)
                    {
                        DataGridCell? cell=VisualsForRow(item).FirstOrDefault(c=>c.Column.DisplayIndex==column);
                        TextBox? editor=cell is null?null:FindVisualChildren<TextBox>(cell).FirstOrDefault();
                        editor?.Focus();
                        editor?.SelectAll();
                    }
                }));
            return;
        }

        if (e.Key == Key.Delete)
        {
            e.Handled = true;
            ClearSelectedCells();
            return;
        }

        if (e.Key is Key.Enter or Key.Tab)
        {
            if (!TryCurrentCoordinate(out int row,out int column)) return;
            e.Handled=true;
            SheetGrid.CommitEdit(DataGridEditingUnit.Cell,true);
            SheetGrid.CommitEdit(DataGridEditingUnit.Row,true);
            (int nextRow,int nextColumn)=NextNavigationCell(row,column,e.Key,shift,SheetGrid.Columns.Count);
            if(nextRow>=0 && nextRow<CurrentSheet!.Grid.Rows.Count)
                Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>NavigateTo(nextRow,nextColumn)));
            return;
        }

        if (e.Key is Key.Home or Key.End ||
            (control && e.Key is Key.Left or Key.Right or Key.Up or Key.Down))
        {
            if (!TryCurrentCoordinate(out int row,out int column) || CurrentSheet is not WorksheetSession sheet) return;
            if(control)
            {
                if(e.Key==Key.Home) { row=0; column=0; }
                else if(e.Key==Key.End) FindLastPopulatedCell(sheet,out row,out column);
                else MoveToDataEdge(sheet,ref row,ref column,e.Key);
            }
            else if(e.Key==Key.Home) column=0;
            else column=SheetGrid.Columns.Count-1;
            e.Handled=true;
            NavigateTo(row,column);
        }
    }

    internal enum GridShortcut { None, Copy, Paste, FillDown, FillRight }

    internal static GridShortcut GetGridShortcut(Key key,ModifierKeys modifiers)
    {
        if((modifiers&ModifierKeys.Control)==0) return GridShortcut.None;
        return key switch
        {
            Key.C=>GridShortcut.Copy,
            Key.V=>GridShortcut.Paste,
            Key.D=>GridShortcut.FillDown,
            Key.R=>GridShortcut.FillRight,
            _=>GridShortcut.None
        };
    }

    internal static (int Row,int Column) NextNavigationCell(int row,int column,Key key,bool shift,int columnCount)
    {
        if(key==Key.Enter) return (row+(shift?-1:1),column);
        column+=shift?-1:1;
        if(column<0) return (row-1,columnCount-1);
        if(column>=columnCount) return (row+1,0);
        return (row,column);
    }

    private IEnumerable<DataGridCell> VisualsForRow(DataRowView item) =>
        FindVisualChildren<DataGridCell>(SheetGrid).Where(cell=>ReferenceEquals(cell.DataContext,item));

    private void CopySelection()
    {
        if(CurrentSheet is not WorksheetSession sheet || !SelectedRange(out CellRange range)) return;
        string text=string.Join("\r\n",Enumerable.Range(range.StartRow,range.EndRow-range.StartRow+1)
            .Select(row=>string.Join("\t",Enumerable.Range(range.StartColumn,range.EndColumn-range.StartColumn+1)
                .Select(column=>sheet.GetInput(row,column)))));
        try { Clipboard.SetText(text); SetStatus($"copied · {range}"); }
        catch(System.Runtime.InteropServices.ExternalException ex) { ShowEditError($"clipboard unavailable · {ex.Message}"); }
    }

    private void FillSelection(bool down)
    {
        if(CurrentSheet is not WorksheetSession sheet || !SelectedRange(out CellRange range)) return;
        int changed=0;
        int rowStart=down?range.StartRow+1:range.StartRow;
        int columnStart=down?range.StartColumn:range.StartColumn+1;
        string? error=null;
        for(int row=rowStart;row<=range.EndRow;++row)
        for(int column=columnStart;column<=range.EndColumn;++column)
        {
            string value=down?sheet.GetInput(range.StartRow,column):sheet.GetInput(row,range.StartColumn);
            if(sheet.CommitCell(row,column,value,out string message)) ++changed;
            else error=$"cell {CellName(row,column)} · {message}";
        }
        if(error is not null) ShowEditError(error); else ClearEditError();
        SetStatus($"filled {changed:N0} cells · {(down?"down":"right")}");
        RefreshEngineText(); SheetGrid.Items.Refresh(); RefreshRealizedCellStyles();
    }

    private void MoveToDataEdge(WorksheetSession sheet,ref int row,ref int column,Key key)
    {
        int rowStep=key==Key.Up?-1:key==Key.Down?1:0;
        int columnStep=key==Key.Left?-1:key==Key.Right?1:0;
        bool occupied=sheet.GetInput(row,column).Length!=0;
        while(true)
        {
            int nextRow=row+rowStep,nextColumn=column+columnStep;
            if(nextRow<0 || nextRow>=sheet.Grid.Rows.Count || nextColumn<0 || nextColumn>=sheet.Grid.Columns.Count) return;
            bool nextOccupied=sheet.GetInput(nextRow,nextColumn).Length!=0;
            if(occupied && !nextOccupied) return;
            row=nextRow; column=nextColumn;
            if(!occupied && nextOccupied) return;
        }
    }

    private static void FindLastPopulatedCell(WorksheetSession sheet,out int row,out int column)
    {
        row=0; column=0;
        for(int r=0;r<sheet.Grid.Rows.Count;++r)
        for(int c=0;c<sheet.Grid.Columns.Count;++c)
            if(sheet.GetInput(r,c).Length!=0) { row=r; column=c; }
    }

    private void PasteClipboard()
    {
        WorksheetSession? sheet = CurrentSheet;
        if (sheet is null ||
            !Clipboard.ContainsText() ||
            !TryCurrentCoordinate(out int startRow, out int startColumn))
        {
            return;
        }

        string clipboard = Clipboard.GetText();
        string[] lines = clipboard
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        int written = 0;
        string lastMessage = string.Empty;
        string? error=null;

        for (int rowOffset = 0; rowOffset < lines.Length; rowOffset++)
        {
            if (rowOffset == lines.Length - 1 && lines[rowOffset].Length == 0)
            {
                continue;
            }

            string[] cells = lines[rowOffset].Split('\t');
            for (int columnOffset = 0; columnOffset < cells.Length; columnOffset++)
            {
                int row = startRow + rowOffset;
                int column = startColumn + columnOffset;

                if (row >= sheet.Grid.Rows.Count || column >= sheet.Grid.Columns.Count)
                {
                    error="paste exceeds worksheet bounds";
                    continue;
                }

                if(sheet.CommitCell(row, column, cells[columnOffset], out lastMessage)) written++;
                else error=$"cell {CellName(row,column)} · {lastMessage}";
            }
        }

        if(error is null) ClearEditError(); else ShowEditError(error);
        SetStatus(error is null?$"pasted {written:N0} cells · {lastMessage}":$"pasted {written:N0} cells · {error}");
        RefreshEngineText();
        SheetGrid.Items.Refresh();
        UpdateFormulaBar();
    }

    private void ClearSelectedCells()
    {
        WorksheetSession? sheet = CurrentSheet;
        if (sheet is null || SheetGrid.SelectedCells.Count == 0)
        {
            return;
        }

        List<(int Row, int Column)> cells = new();

        foreach (DataGridCellInfo cell in SheetGrid.SelectedCells)
        {
            if (cell.Item is not DataRowView rowView || cell.Column is null)
            {
                continue;
            }

            int row = sheet.Grid.Rows.IndexOf(rowView.Row);
            int column = cell.Column.DisplayIndex;

            if (row >= 0 && column >= 0)
            {
                cells.Add((row, column));
            }
        }

        string lastMessage = string.Empty;
        string? error=null;
        foreach ((int row, int column) in cells.Distinct())
        {
            if(!sheet.CommitCell(row, column, string.Empty, out lastMessage))
                error=$"cell {CellName(row,column)} · {lastMessage}";
        }

        if(error is null) ClearEditError(); else ShowEditError(error);
        SetStatus($"cleared {cells.Distinct().Count():N0} cells · {lastMessage}");
        RefreshEngineText();
        SheetGrid.Items.Refresh();
        UpdateFormulaBar();
    }

    private List<(int Row, int Column)> SelectedCoordinates()
    {
        List<(int Row, int Column)> cells = new();
        WorksheetSession? sheet = CurrentSheet;
        if (sheet is null) return cells;

        foreach (DataGridCellInfo cell in SheetGrid.SelectedCells)
        {
            if (cell.Item is not DataRowView rowView || cell.Column is null) continue;
            int row = sheet.Grid.Rows.IndexOf(rowView.Row);
            int column = cell.Column.DisplayIndex;
            if (row >= 0 && column >= 0) cells.Add((row, column));
        }

        if (cells.Count == 0 && TryCurrentCoordinate(out int currentRow, out int currentColumn))
            cells.Add((currentRow, currentColumn));
        return cells.Distinct().ToList();
    }

    private void ApplySelectedFormat(CellFormat format)
    {
        WorksheetSession? sheet = CurrentSheet;
        List<(int Row, int Column)> cells = SelectedCoordinates();
        if (sheet is null || cells.Count == 0) { SetStatus("select one or more cells"); return; }
        sheet.ApplyFormat(cells, format, out string message);
        RefreshCellStyles(sheet);
        SetStatus(message);
    }

    private void ClearFormat_Click(object sender, RoutedEventArgs e)
    {
        WorksheetSession? sheet = CurrentSheet;
        List<(int Row, int Column)> cells = SelectedCoordinates();
        if (sheet is null || cells.Count == 0) { SetStatus("select one or more cells"); return; }
        sheet.ClearFormat(cells, out string message);
        RefreshCellStyles(sheet);
        SetStatus(message);
    }

    private void TableStyle_Click(object sender, RoutedEventArgs e)
    {
        WorksheetSession? sheet = CurrentSheet;
        List<(int Row, int Column)> cells = SelectedCoordinates();
        if (sheet is null || cells.Count == 0) { SetStatus("select a rectangular range"); return; }

        int minRow = cells.Min(x => x.Row), maxRow = cells.Max(x => x.Row);
        int minColumn = cells.Min(x => x.Column), maxColumn = cells.Max(x => x.Column);
        int expected = (maxRow - minRow + 1) * (maxColumn - minColumn + 1);
        if (cells.Count != expected)
        {
            SetStatus("table style requires one rectangular selection");
            return;
        }

        sheet.ApplyTableFormat(new CellRange(minRow, minColumn, maxRow, maxColumn), out string message);
        RefreshCellStyles(sheet);
        SetStatus(message);
    }

    private void RefreshCellStyles(WorksheetSession sheet)
    {
        RefreshRealizedCellStyles();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RefreshRealizedCellStyles));
    }

    private void RefreshRealizedCellStyles()
    {
        foreach (DataGridCell cell in FindVisualChildren<DataGridCell>(SheetGrid))
            ApplyCellVisualFormat(cell);
    }

    private void DataGridCell_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is DataGridCell cell) ApplyCellVisualFormat(cell);
    }

    private void ApplyCellVisualFormat(DataGridCell cell)
    {
        WorksheetSession? sheet = CurrentSheet;
        if (sheet is null || cell.DataContext is not DataRowView rowView || cell.Column is null) return;
        int row = sheet.Grid.Rows.IndexOf(rowView.Row);
        int column = cell.Column.DisplayIndex;
        if (row < 0 || column < 0) return;
        string baseText = sheet.Grid.Rows[row][column]?.ToString() ?? string.Empty;
        CellVisualFormat.Apply(cell, sheet.GetEffectiveFormat(row, column), baseText);
        if (SheetGrid.FrozenColumnCount > 0 && column == SheetGrid.FrozenColumnCount-1) {
            cell.BorderBrush = (Brush)FindResource("AccentBrush"); cell.BorderThickness = new Thickness(0,0,3,0);
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (T nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }

    private void UndoCurrentSheet()
    {
        WorksheetSession? sheet = CurrentSheet;
        if (sheet is null) return;
        sheet.Undo(out string message);
        SetStatus(message);
        RefreshCellStyles(sheet);
        UpdateFormulaBar();
        RefreshEngineText();
    }

    private void RedoCurrentSheet()
    {
        WorksheetSession? sheet = CurrentSheet;
        if (sheet is null) return;
        sheet.Redo(out string message);
        SetStatus(message);
        RefreshCellStyles(sheet);
        UpdateFormulaBar();
        RefreshEngineText();
    }

    private void FindNextInput()
    {
        WorksheetSession? sheet = CurrentSheet;
        string query = FindBox.Text;
        if (sheet is null || string.IsNullOrEmpty(query))
        {
            SetStatus("enter text to find");
            return;
        }

        int startRow = -1;
        int startColumn = -1;
        TryCurrentCoordinate(out startRow, out startColumn);
        if (!sheet.TryFindInput(query, startRow, startColumn, out int row, out int column))
        {
            SetStatus($"not found · {query}");
            return;
        }

        DataRowView item = sheet.Grid.DefaultView[row];
        SheetGrid.SelectedCells.Clear();
        SheetGrid.CurrentCell = new DataGridCellInfo(item, SheetGrid.Columns[column]);
        SheetGrid.SelectedCells.Add(SheetGrid.CurrentCell);
        SheetGrid.ScrollIntoView(item, SheetGrid.Columns[column]);
        NameBox.Text = CellName(row, column);
        FormulaBox.Text = sheet.GetInput(row, column);
        SetStatus($"found · {CellName(row, column)}");
    }

    private void SheetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SheetList.SelectedItem is WorksheetSession sheet)
        {
            BindSheet(sheet);
        }
    }

    private void NameBox_KeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key!=Key.Enter) return;
        e.Handled=true;
        if(!TryParseCellName(NameBox.Text,out int row,out int column) ||
            CurrentSheet is not WorksheetSession sheet || row>=sheet.Grid.Rows.Count || column>=sheet.Grid.Columns.Count)
        { ShowEditError("Name Box address is outside this worksheet"); return; }
        ClearEditError();
        NavigateTo(row,column);
    }

    private static bool TryParseCellName(string text,out int row,out int column)
    {
        row=-1; column=-1;
        string address=text.Trim().ToUpperInvariant();
        int i=0;
        long columnNumber=0;
        while(i<address.Length && address[i] is >= 'A' and <= 'Z')
        {
            columnNumber=columnNumber*26+(address[i]-'A'+1);
            if(columnNumber>int.MaxValue) return false;
            ++i;
        }
        if(i==0 || i==address.Length || !int.TryParse(address[i..],NumberStyles.None,CultureInfo.InvariantCulture,out int oneBasedRow) ||
            oneBasedRow<1) return false;
        row=oneBasedRow-1; column=(int)columnNumber-1;
        return true;
    }

    private void NavigateTo(int row,int column)
    {
        if(CurrentSheet is not WorksheetSession sheet || row<0 || row>=sheet.Grid.Rows.Count ||
            column<0 || column>=sheet.Grid.Columns.Count || column>=SheetGrid.Columns.Count) return;
        DataRowView? item=SheetGrid.Items.Cast<object>().OfType<DataRowView>()
            .FirstOrDefault(view=>ReferenceEquals(view.Row,sheet.Grid.Rows[row]));
        if(item is null) return;
        SheetGrid.SelectedCells.Clear();
        SheetGrid.CurrentCell=new DataGridCellInfo(item,SheetGrid.Columns[column]);
        SheetGrid.SelectedCells.Add(SheetGrid.CurrentCell);
        SheetGrid.ScrollIntoView(item,SheetGrid.Columns[column]);
        SheetGrid.Focus();
        NameBox.Text=CellName(row,column);
        FormulaBox.Text=sheet.GetInput(row,column);
    }

    private bool TryCurrentCoordinate(out int row, out int column)
    {
        row = -1;
        column = -1;

        WorksheetSession? sheet = CurrentSheet;
        if (sheet is null ||
            SheetGrid.CurrentItem is not DataRowView rowView ||
            SheetGrid.CurrentCell.Column is null)
        {
            return false;
        }

        row = sheet.Grid.Rows.IndexOf(rowView.Row);
        column = SheetGrid.CurrentCell.Column.DisplayIndex;

        return row >= 0 && column >= 0;
    }

    private static string CellName(int row, int column) =>
        $"{WorksheetSession.ColumnName(column)}{row + 1}";

    private void RefreshEngineText()
    {
        EngineText.Text = CurrentSheet?.EngineSummary ?? "no sheet";
        UpdateEngineCommandState();
        UpdateErrorBanner();
    }

    private void RefreshSelectionStats()
    {
        if(CurrentSheet is not WorksheetSession sheet) { SelectionStatsText.Text=string.Empty; return; }
        List<(int Row,int Column)> cells=SelectedCoordinates();
        double sum=0;
        int count=0;
        foreach((int row,int column) in cells)
        {
            if(!sheet.TryNumber(row,column,out var value)) continue;
            sum+=(double)value.Numerator/value.Denominator;
            ++count;
        }
        string average=count==0?"—":(sum/count).ToString("G6",CultureInfo.InvariantCulture);
        SelectionStatsText.Text=$"Sum {sum.ToString("G6",CultureInfo.InvariantCulture)} · Average {average} · Count {count:N0}";
    }

    private void UpdateEngineCommandState()
    {
        bool available = CurrentSheet?.EngineAvailable == true;
        RecalculateButton.IsEnabled = available;
        VerifyButton.IsEnabled = available;
        CopyProofButton.IsEnabled = available;
        ExportXlsxButton.IsEnabled = available;
        TableTotalsButton.IsEnabled = available;
        AddChartButton.IsEnabled = available;
        ViewChartsButton.IsEnabled = available;
        InsertRowButton.IsEnabled = available;
        DeleteRowButton.IsEnabled = available;
        InsertColumnButton.IsEnabled = available;
        DeleteColumnButton.IsEnabled = available;
        SortAscendingButton.IsEnabled = available;
        SortDescendingButton.IsEnabled = available;
    }

    private void UpdateErrorBanner()
    {
        string engineError = CurrentSheet is { EngineAvailable: false } sheet ? sheet.EngineLoadError ?? sheet.EngineSummary : string.Empty;
        string text = string.Join(Environment.NewLine, new[] { engineError, _editError }.Where(x => !string.IsNullOrWhiteSpace(x)));
        ErrorBannerText.Text = text;
        ErrorBanner.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowEditError(string message)
    {
        _editError = message;
        UpdateErrorBanner();
    }

    private void ClearEditError()
    {
        _editError = null;
        UpdateErrorBanner();
    }

    private void QueueHeaderSelectionUpdate()
    {
        _headerSelectionTimer.Stop();
        _headerSelectionTimer.Start();
    }

    private void WorkbookSheets_CollectionChanged(object? sender,NotifyCollectionChangedEventArgs e)
    {
        if(e.OldItems is not null)
            foreach(WorksheetSession sheet in e.OldItems) sheet.Changed-=Worksheet_Changed;
        if(e.NewItems is not null)
            foreach(WorksheetSession sheet in e.NewItems) sheet.Changed+=Worksheet_Changed;
        ScheduleRecoverySave();
    }

    private void Worksheet_Changed(object? sender,EventArgs e)=>ScheduleRecoverySave();

    private void ScheduleRecoverySave()
    {
        if(!_workbook.HasUnsavedChanges)
        {
            _recoveryDebounceTimer.Stop();
            DeleteRecoverySnapshot();
            return;
        }
        _recoveryDebounceTimer.Stop();
        _recoveryDebounceTimer.Start();
    }

    private void SetStatus(string message)
    {
        StatusText.Text = string.IsNullOrWhiteSpace(message) ? "Ready" : message;
    }

    private string RecoveryRoot
    {
        get
        {
            string? configured=Environment.GetEnvironmentVariable("KHZ_RECOVERY_DIRECTORY");
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"KHZ Sheet","recovery")
                : Path.GetFullPath(configured);
        }
    }

    private void MainWindow_Loaded(object sender,RoutedEventArgs e)
    {
        if(Environment.GetEnvironmentVariable("KHZ_TEST_ARTIFACTS") is not null) return;
        string manifestPath=Path.Combine(RecoveryRoot,"recovery.json");
        if(!File.Exists(manifestPath)) return;
        if(MessageBox.Show(this,"A recovery copy from an earlier session is available. Restore it?",
            "Recover worksheet",MessageBoxButton.YesNo,MessageBoxImage.Warning)==MessageBoxResult.Yes)
            RestoreRecoverySnapshot();
        else
            DeleteRecoverySnapshot();
    }

    private bool SaveRecoverySnapshot()
    {
        if(!_workbook.HasUnsavedChanges) return true;
        string root=RecoveryRoot;
        string generation=Guid.NewGuid().ToString("N");
        string staging=Path.Combine(root,"."+generation+".tmp");
        string final=Path.Combine(root,generation);
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(staging);
            RecoveryEntry[] entries=_workbook.Sheets.Select((sheet,index)=>
            {
                string extension=sheet.EngineAvailable?".xlsx":".csv";
                string fileName=$"sheet-{index+1:D3}{extension}";
                string path=Path.Combine(staging,fileName);
                if(extension==".xlsx")
                {
                    if(!sheet.ExportXlsx(path,out string message)) throw new IOException(message);
                }
                else sheet.SaveCsv(path);
                return new RecoveryEntry(sheet.Name,fileName);
            }).ToArray();
            Directory.Move(staging,final);
            RecoveryManifest manifest=new(1,generation,entries);
            string manifestTemp=Path.Combine(root,"recovery.json.tmp");
            File.WriteAllText(manifestTemp,JsonSerializer.Serialize(manifest));
            File.Move(manifestTemp,Path.Combine(root,"recovery.json"),overwrite:true);
            foreach(string old in Directory.GetDirectories(root))
                if(!string.Equals(old,final,StringComparison.OrdinalIgnoreCase) && !old.EndsWith(".tmp",StringComparison.OrdinalIgnoreCase))
                    Directory.Delete(old,true);
            return true;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException)
        {
            try { if(Directory.Exists(staging)) Directory.Delete(staging,true); } catch(IOException) { }
            ShowEditError($"recovery autosave failed · {ex.Message}");
            SetStatus($"recovery autosave failed · {ex.Message}");
            return false;
        }
    }

    private void RestoreRecoverySnapshot()
    {
        List<WorksheetSession> recovered=new();
        try
        {
            RecoveryManifest? manifest=JsonSerializer.Deserialize<RecoveryManifest>(File.ReadAllText(Path.Combine(RecoveryRoot,"recovery.json")));
            if(manifest is null || manifest.Version!=1 || !Guid.TryParseExact(manifest.Generation,"N",out _) ||
                manifest.Sheets is null || manifest.Sheets.Length==0) throw new InvalidDataException("recovery manifest is invalid");
            if(manifest.Sheets.Any(entry=>entry is null || entry.Name is null || entry.FileName is null ||
                entry.FileName!=Path.GetFileName(entry.FileName) ||
                Path.GetExtension(entry.FileName) is not (".xlsx" or ".csv")))
                throw new InvalidDataException("recovery file name is invalid");
            if(manifest.Sheets.Any(entry=>Path.GetExtension(entry.FileName)==".xlsx") &&
                CurrentSheet?.EngineAvailable!=true)
                throw new InvalidOperationException(CurrentSheet?.EngineLoadError ?? "native engine unavailable");
            string generation=Path.Combine(RecoveryRoot,manifest.Generation);
            foreach(RecoveryEntry entry in manifest.Sheets)
            {
                WorksheetSession sheet=CurrentSheet?.EngineAvailable==true
                    ? new WorksheetSession(entry.Name)
                    : new WorksheetSession(entry.Name,false,CurrentSheet?.EngineLoadError);
                string recoveryPath=Path.Combine(generation,entry.FileName);
                bool loaded=Path.GetExtension(entry.FileName)==".xlsx"
                    ? sheet.LoadXlsx(recoveryPath,out string message)
                    : sheet.LoadCsv(recoveryPath,out message);
                if(!loaded)
                { sheet.Dispose(); throw new InvalidDataException(message); }
                sheet.MarkDirty();
                recovered.Add(sheet);
            }
            foreach(WorksheetSession existing in _workbook.Sheets) existing.Dispose();
            _workbook.Sheets.Clear();
            foreach(WorksheetSession sheet in recovered) _workbook.Sheets.Add(sheet);
            SheetList.SelectedItem=recovered[0];
            SetStatus($"recovered {recovered.Count:N0} worksheet(s) · save to a workbook file");
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException)
        {
            foreach(WorksheetSession sheet in recovered) sheet.Dispose();
            ShowEditError($"recovery restore failed · {ex.Message}");
            SetStatus($"recovery restore failed · {ex.Message}");
        }
    }

    private void DeleteRecoverySnapshot()
    {
        try
        {
            string root=RecoveryRoot;
            string manifestPath=Path.Combine(root,"recovery.json");
            if(File.Exists(manifestPath))
            {
                RecoveryManifest? manifest=JsonSerializer.Deserialize<RecoveryManifest>(File.ReadAllText(manifestPath));
                if(manifest is not null && Guid.TryParseExact(manifest.Generation,"N",out _))
                {
                    string generation=Path.Combine(root,manifest.Generation);
                    if(Directory.Exists(generation)) Directory.Delete(generation,true);
                }
                File.Delete(manifestPath);
            }
            string tempManifest=Path.Combine(root,"recovery.json.tmp");
            if(File.Exists(tempManifest)) File.Delete(tempManifest);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException)
        { ShowEditError($"recovery cleanup failed · {ex.Message}"); }
    }

    private bool ConfirmContinueWithUnsavedChanges(string action)
    {
        if(!_workbook.HasUnsavedChanges) return true;
        if(MessageBox.Show(this,$"There are unsaved changes. A recovery copy will be written before you {action}. Continue?",
            "Unsaved changes",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes) return false;
        return SaveRecoverySnapshot();
    }

    private void MainWindow_Closing(object? sender,System.ComponentModel.CancelEventArgs e)
    {
        if(!_workbook.HasUnsavedChanges) { DeleteRecoverySnapshot(); return; }
        if(Environment.GetEnvironmentVariable("KHZ_TEST_ARTIFACTS") is not null)
        {
            SaveRecoverySnapshot();
            return;
        }
        MessageBoxResult answer=MessageBox.Show(this,"Save a crash-recovery copy before closing? Choose No to discard unsaved changes.",
            "Unsaved changes",MessageBoxButton.YesNoCancel,MessageBoxImage.Warning);
        if(answer==MessageBoxResult.Cancel) { e.Cancel=true; return; }
        if(answer==MessageBoxResult.Yes && !SaveRecoverySnapshot()) { e.Cancel=true; return; }
        if(answer==MessageBoxResult.No) DeleteRecoverySnapshot();
    }

    private static string SafeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
        return string.IsNullOrWhiteSpace(safe) ? "Sheet" : safe;
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _recoveryTimer.Stop();
        _recoveryDebounceTimer.Stop();
        _headerSelectionTimer.Stop();
        _workbook.Dispose();
    }
}
