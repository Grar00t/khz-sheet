using System.Data;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using KHZ.Sheet.Core;
using CellRange = KHZ.Sheet.Desktop.CellRange;

namespace KHZ.Sheet.Desktop;

public partial class MainWindow
{
    private async void OpenXlsx_Click(object sender,RoutedEventArgs e)
    {
        if(!ConfirmContinueWithUnsavedChanges("open another worksheet")) return;
        OpenFileDialog dialog=new() {Title="Open XLSX",Filter="XLSX (*.xlsx)|*.xlsx",CheckFileExists=true};
        if(dialog.ShowDialog(this)!=true) return;
        IsEnabled=false; SetStatus("Reading and validating XLSX…");
        WorksheetSession? candidate=null;
        try {
            var result=await Task.Run(()=> {
                var sheet=new WorksheetSession(Path.GetFileNameWithoutExtension(dialog.FileName));
                bool ok=sheet.LoadXlsx(dialog.FileName,out string message); return (sheet,ok,message);
            });
            candidate=result.sheet;
            if(result.ok) { candidate.MarkDirty(); _workbook.Sheets.Add(candidate); SheetList.SelectedItem=candidate; candidate=null; }
            else ShowEditError(result.message);
            SetStatus(result.message);
        } finally { candidate?.Dispose(); IsEnabled=true; }
    }

    private void RenameSheet_Click(object sender,RoutedEventArgs e)
    {
        if(CurrentSheet is not WorksheetSession sheet) return;
        string? name=PromptSheetName(sheet.Name);
        if(name is null) return;
        bool ok=_workbook.TryRenameSheet(sheet,name,out string message);
        SetStatus(message);
        if(!ok) ShowEditError(message);
    }

    private void DuplicateSheet_Click(object sender,RoutedEventArgs e)
    {
        if(CurrentSheet is not WorksheetSession sheet) return;
        WorksheetSession? copy=_workbook.DuplicateSheet(sheet,out string message);
        if(copy is null) { SetStatus(message); ShowEditError(message); return; }
        SheetList.SelectedItem=copy;
        SetStatus(message);
    }

    private void DeleteSheet_Click(object sender,RoutedEventArgs e)
    {
        if(CurrentSheet is not WorksheetSession sheet) return;
        if(MessageBox.Show(this,$"Delete sheet '{sheet.Name}'? This cannot be undone.",
            "Delete worksheet",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes) return;
        if(sheet.IsDirty && MessageBox.Show(this,"This sheet has unsaved changes. Delete it without saving?",
            "Unsaved worksheet",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes) return;
        int oldIndex=SheetList.SelectedIndex;
        if(!_workbook.RemoveSheet(sheet,out string message)) { SetStatus(message); return; }
        SheetList.SelectedIndex=Math.Min(oldIndex,SheetList.Items.Count-1);
        SetStatus(message);
    }

    private void MoveSheetLeft_Click(object sender,RoutedEventArgs e)=>MoveCurrentSheet(-1);
    private void MoveSheetRight_Click(object sender,RoutedEventArgs e)=>MoveCurrentSheet(1);

    private void MoveCurrentSheet(int offset)
    {
        if(CurrentSheet is not WorksheetSession sheet) return;
        int target=SheetList.SelectedIndex+offset;
        if(_workbook.MoveSheet(sheet,target,out string message)) SheetList.SelectedIndex=target;
        SetStatus(message);
    }

    private string? PromptSheetName(string currentName)
    {
        TextBox input=new() {Text=currentName,MinWidth=260,Margin=new Thickness(0,0,0,10)};
        Button accept=new() {Content="Rename",IsDefault=true,MinWidth=80,Margin=new Thickness(0,0,8,0)};
        Button cancel=new() {Content="Cancel",IsCancel=true,MinWidth=80};
        Window dialog=new() {Title="Rename worksheet",Owner=this,WindowStartupLocation=WindowStartupLocation.CenterOwner,
            SizeToContent=SizeToContent.WidthAndHeight,ResizeMode=ResizeMode.NoResize};
        StackPanel panel=new() {Margin=new Thickness(14)};
        panel.Children.Add(input);
        StackPanel buttons=new() {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        buttons.Children.Add(accept); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        accept.Click+=(_,_)=>dialog.DialogResult=true;
        dialog.Content=panel;
        dialog.Loaded+=(_,_)=>{input.Focus();input.SelectAll();};
        return dialog.ShowDialog()==true?input.Text:null;
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

    private void InsertRow_Click(object sender,RoutedEventArgs e)=>ChangeStructure(StructureAxis.Row,insert:true);
    private void DeleteRow_Click(object sender,RoutedEventArgs e)=>ChangeStructure(StructureAxis.Row,insert:false);
    private void InsertColumn_Click(object sender,RoutedEventArgs e)=>ChangeStructure(StructureAxis.Column,insert:true);
    private void DeleteColumn_Click(object sender,RoutedEventArgs e)=>ChangeStructure(StructureAxis.Column,insert:false);

    private enum StructureAxis { Row, Column }

    private void ChangeStructure(StructureAxis axis,bool insert)
    {
        if(CurrentSheet is not WorksheetSession sheet || !TryCurrentCoordinate(out int row,out int column)) return;
        int index=axis==StructureAxis.Row?row:column;
        string noun=axis==StructureAxis.Row?"row":"column";
        if(!insert && MessageBox.Show(this,$"Delete {noun} {index+1}? Cells and dependent references will be shifted.",
            "Confirm deletion",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes) return;
        string message;
        bool ok=axis switch
        {
            StructureAxis.Row when insert=>sheet.InsertRow(index,out message),
            StructureAxis.Row=>sheet.DeleteRow(index,out message),
            StructureAxis.Column when insert=>sheet.InsertColumn(index,out message),
            _=>sheet.DeleteColumn(index,out message)
        };
        if(!ok) { ShowEditError(message); SetStatus(message); return; }
        _editError=null;
        SheetGrid.ItemsSource=sheet.Grid.DefaultView;
        SheetGrid.Items.Refresh();
        RefreshCellStyles(sheet);
        int nextRow=axis==StructureAxis.Row?(insert&&row>=index?row+1:!insert&&row>index?row-1:row):row;
        int nextColumn=axis==StructureAxis.Column?(insert&&column>=index?column+1:!insert&&column>index?column-1:column):column;
        if(!insert && (axis==StructureAxis.Row?row==index:column==index))
        { nextRow=Math.Min(nextRow,sheet.Grid.Rows.Count-1); nextColumn=Math.Min(nextColumn,sheet.Grid.Columns.Count-1); }
        NavigateTo(nextRow,nextColumn);
        RefreshEngineText();
        SetStatus($"{(insert?"inserted":"deleted")} {noun} · formulas adjusted; undo history cleared");
    }

    private void SheetGrid_ColumnResizeCompleted(object sender,DragCompletedEventArgs e)
    {
        if(CurrentSheet is not WorksheetSession sheet ||
            FindVisualAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject)?.Column is not { } column ||
            column.ActualWidth < 12) return;
        sheet.SetColumnWidth(column.DisplayIndex,(column.ActualWidth-5)/7);
    }

    private void SheetGrid_ColumnAutoFit(object sender,MouseButtonEventArgs e)
    {
        if(CurrentSheet is not WorksheetSession sheet ||
            FindVisualAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject)?.Column is not { } column) return;
        column.Width=DataGridLength.SizeToCells;
        Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>
        {
            if(column.ActualWidth>=12)
                sheet.SetColumnWidth(column.DisplayIndex,Math.Clamp((column.ActualWidth-5)/7,1,255));
        }));
    }

    private void UpdateHeaderSelection()
    {
        var cells=SelectedCoordinates();var rows=cells.Select(c=>c.Row).ToHashSet();var cols=cells.Select(c=>c.Column).ToHashSet();
        foreach(var header in FindVisualChildren<DataGridColumnHeader>(SheetGrid)) {
            header.Tag=header.Column is not null && cols.Contains(header.Column.DisplayIndex);
            AutomationProperties.SetHelpText(header,(bool)header.Tag?"Selected column":"Column");
        }
        foreach(var header in FindVisualChildren<DataGridRowHeader>(SheetGrid)) {
            var visual=FindVisualAncestor<DataGridRow>(header);
            int row=visual?.Item is DataRowView item && CurrentSheet is WorksheetSession sheet?sheet.Grid.Rows.IndexOf(item.Row):-1;
            header.Tag=rows.Contains(row);
        }
    }

    private static T? FindVisualAncestor<T>(DependencyObject? current) where T:DependencyObject
    {
        while(current is not null) {
            if(current is T match) return match;
            DependencyObject? parent=VisualTreeHelper.GetParent(current);
            if(parent is null && current is FrameworkElement element) parent=element.TemplatedParent;
            current=parent;
        }
        return null;
    }
}
