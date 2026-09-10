using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace KHZ.Sheet.Desktop;

public partial class MainWindow : Window
{
    private readonly WorkbookSession _workbook = new();
    private bool _rebinding;

    public MainWindow()
    {
        InitializeComponent();

        SheetList.ItemsSource = _workbook.Sheets;
        SheetList.SelectedIndex = 0;

        if (SheetList.SelectedItem is WorksheetSession sheet)
        {
            BindSheet(sheet);
        }

        Closed += MainWindow_Closed;
    }

    private WorksheetSession? CurrentSheet => SheetList.SelectedItem as WorksheetSession;

    private void BindSheet(WorksheetSession sheet)
    {
        _rebinding = true;
        try
        {
            SheetGrid.ItemsSource = sheet.Grid.DefaultView;
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

        WorksheetSession sheet = _workbook.AddSheet(Path.GetFileNameWithoutExtension(dialog.FileName));
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
        e.Row.Header = (e.Row.GetIndex() + 1).ToString();
    }

    private void SheetGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        e.Column.Width = new DataGridLength(110);
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

        int row = e.Row.GetIndex();
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
                    SetStatus($"cell {CellName(row, column)} · {message}");
                }

                FormulaBox.Text = sheet.GetInput(row, column);
                RefreshEngineText();
                SheetGrid.Items.Refresh();
            }));
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

        int written = 0;
        string lastMessage = string.Empty;

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
                    continue;
                }

                sheet.CommitCell(row, column, cells[columnOffset], out lastMessage);
                written++;
            }
        }

        SetStatus($"pasted {written:N0} cells · {lastMessage}");
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
        foreach ((int row, int column) in cells.Distinct())
        {
            sheet.CommitCell(row, column, string.Empty, out lastMessage);
        }

        SetStatus($"cleared {cells.Distinct().Count():N0} cells · {lastMessage}");
        RefreshEngineText();
        SheetGrid.Items.Refresh();
        UpdateFormulaBar();
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

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _workbook.Dispose();
    }
}
