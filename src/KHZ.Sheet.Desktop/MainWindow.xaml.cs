using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
    private bool _rebinding;
    private bool _formatControlReady;

    private sealed record ColorChoice(string Name, string? Hex);
    private sealed record NumberFormatChoice(string Name, CellNumberFormat Format);

    public MainWindow()
    {
        InitializeComponent();
        SheetGrid.AddHandler(FrameworkElement.LoadedEvent, new RoutedEventHandler(DataGridCell_Loaded));
        InitializeFormattingControls();
        ThemeBox.ItemsSource = SheetTheme.Presets; ThemeBox.DisplayMemberPath = nameof(SheetTheme.Name);
        ChartKindBox.ItemsSource = Enum.GetValues<ChartKind>(); ChartKindBox.SelectedIndex = 1;

        SheetList.ItemsSource = _workbook.Sheets;
        SheetList.SelectedIndex = 0;

        if (SheetList.SelectedItem is WorksheetSession sheet)
        {
            BindSheet(sheet);
        }

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
        }
        finally
        {
            _rebinding = false;
        }
    }

    private void NewSheet_Click(object sender, RoutedEventArgs e)
    {
        WorksheetSession sheet = _workbook.AddSheet();
        SheetList.SelectedItem = sheet;
        SetStatus($"created · {sheet.Name}");
    }

    private WorksheetSession? ContextSheet(object sender) =>
        sender is MenuItem { Parent: ContextMenu { PlacementTarget: ListBoxItem item } }
            ? item.DataContext as WorksheetSession
            : null;

    private void SheetTabRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item) item.IsSelected = true;
    }

    private string? PromptSheetName(string title, string initialName)
    {
        Window dialog = new()
        {
            Title = title,
            Owner = this,
            Width = 360,
            Height = 160,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Style = (Style)FindResource(typeof(Window))
        };
        TextBox input = new() { Text = initialName, Margin = new Thickness(0, 8, 0, 12) };
        Button accept = new() { Content = "OK", Width = 84, IsDefault = true };
        Button cancel = new() { Content = "Cancel", Width = 84, IsCancel = true };
        accept.Click += (_, _) => dialog.DialogResult = true;
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(accept);
        buttons.Children.Add(cancel);
        StackPanel content = new() { Margin = new Thickness(16) };
        content.Children.Add(new TextBlock { Text = "Worksheet name (1-31 characters)" });
        content.Children.Add(input);
        content.Children.Add(buttons);
        dialog.Content = content;
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text : null;
    }

    private void SheetRename_Click(object sender, RoutedEventArgs e)
    {
        WorksheetSession? sheet = ContextSheet(sender);
        if (sheet is null) return;
        string? name = PromptSheetName("Rename worksheet", sheet.Name);
        if (name is null) return;
        if (_workbook.RenameSheet(sheet, name, out string message))
        {
            SheetList.Items.Refresh();
            SetStatus(message);
        }
        else SetStatus(message);
    }

    private void SheetDuplicate_Click(object sender, RoutedEventArgs e)
    {
        WorksheetSession? sheet = ContextSheet(sender);
        if (sheet is null) return;
        if (_workbook.DuplicateSheet(sheet, null, out WorksheetSession? duplicate, out string message))
        {
            SheetList.SelectedItem = duplicate;
            SetStatus(message);
        }
        else SetStatus(message);
    }

    private void SheetMoveLeft_Click(object sender, RoutedEventArgs e)
    {
        WorksheetSession? sheet = ContextSheet(sender);
        if (sheet is null) return;
        _workbook.MoveSheet(sheet, -1, out string message);
        SheetList.SelectedItem = sheet;
        SetStatus(message);
    }

    private void SheetMoveRight_Click(object sender, RoutedEventArgs e)
    {
        WorksheetSession? sheet = ContextSheet(sender);
        if (sheet is null) return;
        _workbook.MoveSheet(sheet, 1, out string message);
        SheetList.SelectedItem = sheet;
        SetStatus(message);
    }

    private void SheetDelete_Click(object sender, RoutedEventArgs e)
    {
        WorksheetSession? sheet = ContextSheet(sender);
        if (sheet is null) return;
        if (MessageBox.Show(this, $"Delete worksheet '{sheet.Name}'?", "Delete worksheet",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        int index = SheetList.Items.IndexOf(sheet);
        if (!_workbook.DeleteSheet(sheet, out string message))
        {
            SetStatus(message);
            return;
        }
        if (SheetList.SelectedItem is not WorksheetSession)
            SheetList.SelectedIndex = Math.Min(Math.Max(index - 1, 0), SheetList.Items.Count - 1);
        SetStatus(message);
    }

    private void OpenCsv_Click(object sender, RoutedEventArgs e)
    {
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

        WorksheetSession sheet = _workbook.AddSheet(SafeSheetNameFromPath(dialog.FileName));
        SheetList.SelectedItem = sheet;

        try
        {
            if (sheet.LoadCsv(dialog.FileName, out string message))
            {
                SetStatus(message);
                RefreshEngineText();
            }
            else
            {
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
            SetStatus($"saved · {dialog.FileName}");
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
            SetStatus(message);
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

        sheet.Recalculate(out string message);
        SetStatus(message);
        RefreshEngineText();
    }

    private void Verify_Click(object sender, RoutedEventArgs e)
    {
        WorksheetSession? sheet = CurrentSheet;
        if (sheet is null)
        {
            return;
        }

        sheet.Verify(out string message);
        SetStatus(message);
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

    private void FreezeSelection_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentSheet is not WorksheetSession sheet ||
            !TryCurrentCoordinate(out _, out int column) || column == 0)
        {
            SetStatus("select a cell in column B or later to freeze preceding columns");
            return;
        }
        int columns = SheetGrid.FrozenColumnCount == column ? 0 : column;
        SheetGrid.FrozenColumnCount = columns;
        sheet.SetFrozenColumns(columns);
        RefreshRealizedCellStyles();
        SetStatus(columns == 0 ? "frozen columns cleared" : $"columns A:{WorksheetSession.ColumnName(columns - 1)} frozen");
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
            SetStatus(message);
        }
        else
        {
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
        UpdateHeaderSelection();
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
                    SetStatus(message);
                }
                else
                {
                    sheet.Grid.Rows[row][column] = sheet.RenderInput(row, column);
                    SetStatus($"cell {CellName(row, column)} · {message}");
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
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.V)
        {
            e.Handled = true;
            PasteClipboard();
            return;
        }

        if (e.Key == Key.Delete)
        {
            e.Handled = true;
            ClearSelectedCells();
        }
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

        List<(int Row, int Column, string? Input)> edits = new();
        for (int rowOffset = 0; rowOffset < lines.Length; rowOffset++)
        {
            if (rowOffset == lines.Length - 1 && lines[rowOffset].Length == 0) continue;
            string[] cells = lines[rowOffset].Split('\t');
            for (int columnOffset = 0; columnOffset < cells.Length; columnOffset++)
                edits.Add((startRow + rowOffset, startColumn + columnOffset, cells[columnOffset]));
        }

        bool pasted = sheet.CommitCellsAtomic(edits, out string message);
        SetStatus(pasted ? $"pasted {edits.Count:N0} cells · one undo action" : $"paste refused · {message}");
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

        var edits = cells.Distinct().Select(cell => (cell.Row, cell.Column, (string?)string.Empty)).ToArray();
        bool cleared = sheet.CommitCellsAtomic(edits, out string message);
        SetStatus(cleared ? $"cleared {edits.Length:N0} cells · one undo action" : $"clear refused · {message}");
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
        if (e.OriginalSource is DataGridCell cell) ApplyCellVisualFormat(cell);
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
    }

    private void SetStatus(string message)
    {
        StatusText.Text = string.IsNullOrWhiteSpace(message) ? "Ready" : message;
    }

    private static string SafeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
        return string.IsNullOrWhiteSpace(safe) ? "Sheet" : safe;
    }

    private static string SafeSheetNameFromPath(string path)
    {
        string raw = Path.GetFileNameWithoutExtension(path);
        char[] invalid = [':', '\\', '/', '?', '*', '[', ']'];
        string name = string.Concat(raw.Select(c => invalid.Contains(c) ? '_' : c)).Trim('\'');
        if (name.Length > 31) name = name[..31].TrimEnd('\'');
        return name.Length == 0 ? "Imported" : name;
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _workbook.Dispose();
    }
}
