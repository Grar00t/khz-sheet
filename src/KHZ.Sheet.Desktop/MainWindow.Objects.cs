using System.Data;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using KHZ.Sheet.Core;
using CellRange = KHZ.Sheet.Desktop.CellRange;

namespace KHZ.Sheet.Desktop;

public partial class MainWindow
{
    private async void OpenXlsx_Click(object sender,RoutedEventArgs e)
    {
        OpenFileDialog dialog=new() {Title="Open XLSX",Filter="XLSX (*.xlsx)|*.xlsx",CheckFileExists=true};
        if(dialog.ShowDialog(this)!=true) return;
        IsEnabled=false; SetStatus("Reading and validating XLSX…");
        string sheetName=_workbook.GetUniqueSheetName(SafeSheetNameFromPath(dialog.FileName));
        WorksheetSession? candidate=null;
        try {
            var result=await Task.Run(()=> {
                var sheet=new WorksheetSession(sheetName);
                bool ok=sheet.LoadXlsx(dialog.FileName,out string message); return (sheet,ok,message);
            });
            candidate=result.sheet;
            if(result.ok) { _workbook.Sheets.Add(candidate); SheetList.SelectedItem=candidate; candidate=null; }
            SetStatus(result.message);
        } finally { candidate?.Dispose(); IsEnabled=true; }
    }
    private void ApplyThemeResources(SheetTheme? theme)
    {
        if(theme is null)
        {
            foreach(string key in new[] {"WindowBrush","TextBrush","MutedTextBrush","PanelBrush","PanelRaisedBrush","GridBrush","AccentBrush","AccentHoverBrush","SelectionBrush","SelectedTextBrush"}) Resources.Remove(key);
            return;
        }
        var map=new Dictionary<string,string> { ["WindowBrush"]=theme.Background,["TextBrush"]=theme.Text,
            ["MutedTextBrush"]=theme.Text,["PanelBrush"]=theme.Panel,["PanelRaisedBrush"]=theme.Band,
            ["GridBrush"]=theme.Grid,["AccentBrush"]=theme.Accent,["AccentHoverBrush"]=theme.Accent,["SelectionBrush"]=theme.Accent,
            ["SelectedTextBrush"]=theme.Name=="High Contrast"?"#000000":"#FFFFFF" };
        foreach(var pair in map) Resources[pair.Key]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Value));
    }
    private void ThemeBox_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(_rebinding || CurrentSheet is not WorksheetSession sheet || ThemeBox.SelectedItem is not SheetTheme theme) return;
        sheet.SetTheme(theme); ApplyThemeResources(theme); RefreshFormulaSyntax(); RefreshCellStyles(sheet); SetStatus(theme.Name);
    }
    private void FormulaBox_TextChanged(object sender,TextChangedEventArgs e) => RefreshFormulaSyntax();
    private void RefreshFormulaSyntax()
    {
        if(FormulaSyntax is null) return;
        FormulaSyntax.Inlines.Clear(); string source=FormulaBox.Text;
        FormulaSyntax.Visibility=source.StartsWith('=') ? Visibility.Visible : Visibility.Collapsed;
        if(FormulaSyntax.Visibility!=Visibility.Visible) return;
        if(source.Length>8192 || FormulaLexer.TryTokenize(source,out var tokens)!=SheetStatus.Ok) {
            FormulaSyntax.Inlines.Add(new Run(source) {Foreground=Brushes.IndianRed});return;
        }
        int offset=0;
        foreach(var token in tokens) {
            if(token.Start>offset) FormulaSyntax.Inlines.Add(new Run(source[offset..token.Start]));
            bool dark=CurrentSheet?.Theme?.Name is "Monochromatic Dark" or "High Contrast" || CurrentSheet?.Theme is null;
            Brush color=token.Kind switch {
                FormulaTokenKind.Reference=>dark?Brushes.Cyan:Brushes.DarkBlue,FormulaTokenKind.Number=>dark?Brushes.LightGreen:Brushes.DarkGreen,
                FormulaTokenKind.Name=>dark?Brushes.Orchid:Brushes.DarkMagenta,FormulaTokenKind.Operator=>dark?Brushes.Yellow:Brushes.SaddleBrown,
                _=>(Brush)FindResource("TextBrush") };
            FormulaSyntax.Inlines.Add(new Run(source.Substring(token.Start,token.Length)) {Foreground=color});offset=token.Start+token.Length;
        }
    }
    private bool SelectedRange(out CellRange range)
    {
        var cells=SelectedCoordinates();range=default;
        if(cells.Count==0) return false;
        range=new(cells.Min(c=>c.Row),cells.Min(c=>c.Column),cells.Max(c=>c.Row),cells.Max(c=>c.Column));
        return cells.Count==(range.EndRow-range.StartRow+1)*(range.EndColumn-range.StartColumn+1);
    }
    private void CreateTable_Click(object sender,RoutedEventArgs e)
    {
        if(CurrentSheet is not WorksheetSession sheet || !SelectedRange(out var range)) {SetStatus("Select a rectangle with unique text headers");return;}
        int id=1; while(sheet.Tables.Any(t=>t.Name=="Table_"+id)) ++id;
        sheet.CreateTable(range,"Table_"+id,false,out string message);SetStatus(message);RefreshCellStyles(sheet);
    }
    private void CreateTableTotals_Click(object sender,RoutedEventArgs e)
    {
        if(CurrentSheet is not WorksheetSession sheet || !SelectedRange(out var range)) {SetStatus("Select headers and data");return;}
        int id=1; while(sheet.Tables.Any(t=>t.Name=="Table_"+id)) ++id;
        sheet.CreateTableWithTotals(range,"Table_"+id,out string message);
        SetStatus(message);RefreshCellStyles(sheet);
    }
    private void SortAscending_Click(object sender,RoutedEventArgs e)=>SortTable(false);
    private void SortDescending_Click(object sender,RoutedEventArgs e)=>SortTable(true);
    private void ResetSort_Click(object sender,RoutedEventArgs e)
    {
        if(CurrentSheet is WorksheetSession sheet) {SheetGrid.ItemsSource=sheet.Grid.DefaultView;SetStatus("Original coordinate order");}
    }
    private void SortTable(bool descending)
    {
        if(CurrentSheet is not WorksheetSession sheet || !TryCurrentCoordinate(out int row,out int col)) return;
        var table=sheet.Tables.FirstOrDefault(t=>row>=t.Range.TopLeft.Row && row<=t.Range.BottomRight.Row && col>=t.Range.TopLeft.Column && col<=t.Range.BottomRight.Column);
        if(table is null) {SetStatus("Select a cell inside a table");return;}
        if(!SheetGrid.CommitEdit(DataGridEditingUnit.Cell,true) || !SheetGrid.CommitEdit(DataGridEditingUnit.Row,true)) return;
        SheetGrid.ItemsSource=sheet.SortedTableView(table.Id,col,descending);
        SetStatus("Table view sorted · row labels retain native coordinates; export uses original order");
    }
    private void AddChart_Click(object sender,RoutedEventArgs e)
    {
        if(CurrentSheet is not WorksheetSession sheet || !SelectedRange(out var range) || ChartKindBox.SelectedItem is not ChartKind kind) return;
        if(sheet.AddChart(kind,range,out string message)) ShowChart(sheet,sheet.Charts.Last());
        SetStatus(message);
    }
    private void ViewCharts_Click(object sender,RoutedEventArgs e)
    {
        if(CurrentSheet is not WorksheetSession sheet || sheet.Charts.Count==0) {SetStatus("No charts in this worksheet");return;}
        foreach(var chart in sheet.Charts) ShowChart(sheet,chart);
    }
    private void ShowChart(WorksheetSession sheet,ChartDefinition chart)
    {
        var window=new Window {Title=chart.Title,Owner=this,Width=760,Height=520,Background=Brushes.White,Foreground=Brushes.Black};
        var panel=new DockPanel(); var refresh=new Button {Content="Refresh from native values",Foreground=Brushes.Black};
        DockPanel.SetDock(refresh,Dock.Top);panel.Children.Add(refresh);
        var view=new ChartView(chart,sheet.SampleChart(chart));panel.Children.Add(view);
        void Update(object? _, EventArgs e) => view.Update(sheet.SampleChart(chart));
        sheet.ValuesChanged += Update;
        window.Closed += (_,_) => sheet.ValuesChanged -= Update;
        refresh.Click+=(_,_)=>view.Update(sheet.SampleChart(chart));window.Content=panel;window.Show();
    }
    private void RowHeight_Click(object sender,RoutedEventArgs e)
    {
        if(CurrentSheet is not WorksheetSession sheet || !double.TryParse(RowHeightBox.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out double points)) return;
        foreach(int row in SelectedCoordinates().Select(c=>c.Row).Distinct()) sheet.SetRowHeight(row,points);
        foreach(var visual in FindVisualChildren<DataGridRow>(SheetGrid))
            if(visual.Item is DataRowView item && sheet.RowHeights.TryGetValue(sheet.Grid.Rows.IndexOf(item.Row),out double h)) visual.Height=h*96/72;
        SetStatus("Row height stored in points");
    }
    private void SheetGrid_MouseUp(object sender,MouseButtonEventArgs e)
    {
        if(CurrentSheet is not WorksheetSession sheet) return;
        foreach(var column in SheetGrid.Columns) if(column.ActualWidth>=12) sheet.SetColumnWidth(column.DisplayIndex,(column.ActualWidth-5)/7);
        foreach(var visual in FindVisualChildren<DataGridRow>(SheetGrid))
            if(!double.IsNaN(visual.Height) && visual.Item is DataRowView item) sheet.SetRowHeight(sheet.Grid.Rows.IndexOf(item.Row),visual.Height*72/96);
        UpdateHeaderSelection();
    }
    private void UpdateHeaderSelection()
    {
        var cells=SelectedCoordinates();var rows=cells.Select(c=>c.Row).ToHashSet();var cols=cells.Select(c=>c.Column).ToHashSet();
        foreach(var header in FindVisualChildren<DataGridColumnHeader>(SheetGrid)) {
            bool selected=header.Column is not null && cols.Contains(header.Column.DisplayIndex);
            header.FontWeight=selected?FontWeights.Bold:FontWeights.Normal;
            header.BorderThickness=selected?new Thickness(0,0,1,3):new Thickness(0,0,1,1);
            AutomationProperties.SetHelpText(header,selected?"Selected column":"Column");
        }
        foreach(var visual in FindVisualChildren<DataGridRow>(SheetGrid)) {
            int row=visual.Item is DataRowView item && CurrentSheet is WorksheetSession sheet?sheet.Grid.Rows.IndexOf(item.Row):-1;
            visual.HeaderStyle=new Style(typeof(DataGridRowHeader),FindResource(typeof(DataGridRowHeader)) as Style) {
                Setters={new Setter(Control.FontWeightProperty,rows.Contains(row)?FontWeights.Bold:FontWeights.Normal)} };
        }
    }
}
