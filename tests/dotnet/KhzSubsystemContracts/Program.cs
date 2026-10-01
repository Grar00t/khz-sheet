using System.Data;
using System.Collections.ObjectModel;
using System.Windows.Automation.Peers;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using KHZ.Sheet.Core;
using KHZ.Sheet.Desktop;
using CellRange = KHZ.Sheet.Desktop.CellRange;

internal static class Program
{
    private static int checks;
    private static void Check(bool ok,string name) { ++checks;if(!ok)throw new Exception("FAIL "+name);Console.WriteLine("PASS "+name); }
    private static void Edit(WorksheetSession s,int r,int c,string v)=>Check(s.CommitCell(r,c,v,out string m),"edit "+r+","+c+" "+m);
    private static void Rewrite(string source,string target,string entryName,Func<string,string> edit)
    {
        using var input=ZipFile.OpenRead(source);using var output=ZipFile.Open(target,ZipArchiveMode.Create);
        foreach(var entry in input.Entries) {
            using var reader=new StreamReader(entry.Open());string text=reader.ReadToEnd();
            using var writer=new StreamWriter(output.CreateEntry(entry.FullName).Open());writer.Write(entry.FullName==entryName?edit(text):text);
        }
    }
    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T:DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);++i) {
            var child=VisualTreeHelper.GetChild(root,i);
            if(child is T value) yield return value;
            foreach(var nested in Visuals<T>(child)) yield return nested;
        }
    }
    private static void Pump()
    {
        DispatcherFrame frame=new();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));
        Dispatcher.PushFrame(frame);
    }
    private static void Capture(FrameworkElement view,string path)
    {
        view.UpdateLayout();
        RenderTargetBitmap bitmap=new((int)Math.Ceiling(view.ActualWidth),(int)Math.Ceiling(view.ActualHeight),96,96,PixelFormats.Pbgra32);
        bitmap.Render(view); using var file=File.Create(path);
        PngBitmapEncoder encoder=new();encoder.Frames.Add(BitmapFrame.Create(bitmap));encoder.Save(file);
        Check(file.Length>500,"render "+Path.GetFileName(path));
    }
    [STAThread]
    private static int Main()
    {
        string? artifacts=Environment.GetEnvironmentVariable("KHZ_TEST_ARTIFACTS");
        string dir=Path.Combine(artifacts ?? Path.GetTempPath(),"khz-subsystems-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        if(artifacts is not null) Console.WriteLine("ARTIFACTS="+dir);
        try {
            const string loadError="Could not load khz_sheet.dll or the required VC++ runtime: missing native runtime";
            using(var unavailable=new WorksheetSession("Unavailable",false,loadError)) {
                Check(!unavailable.EngineAvailable,"injected native engine failure");
                Check(!unavailable.CommitCell(0,0,"=1+1",out string formulaError) && formulaError==loadError,"unavailable engine refuses formulas with load error");
                Check(!unavailable.CommitCell(0,0,"12.5",out _),"unavailable engine refuses numeric input");
                Check(unavailable.GetInput(0,0)=="","rejected unavailable-engine edits preserve input");
                Check(unavailable.CommitCell(0,0,"plain text",out _) && unavailable.GetInput(0,0)=="plain text","unavailable engine permits text only");
            }
            using(var workbook=new WorkbookSession()) {
                WorksheetSession original=workbook.Sheets[0];
                Check(original.CommitCell(0,0,"7/9",out _),"sheet management source value");
                Check(workbook.TryRenameSheet(original,"Overview",out _) && original.Name=="Overview","sheet rename");
                workbook.AddSheet("Details");
                Check(!workbook.TryRenameSheet(original,"Details",out _) && original.Name=="Overview","duplicate sheet name refused");
                original.ApplyFormat([(0,0)],new CellFormat(Bold:true),out _);
                original.SetTheme(SheetTheme.Presets[1]);original.SetColumnWidth(0,18.5);
                WorksheetSession? duplicate=workbook.DuplicateSheet(original,out _);
                Check(duplicate is not null && duplicate.Name=="Overview Copy" && duplicate.GetInput(0,0)=="7/9" &&
                    duplicate.GetEffectiveFormat(0,0).Bold==true && duplicate.Theme==original.Theme &&
                    duplicate.ColumnWidths[0]==18.5,"duplicate copies cell and presentation state");
                Check(duplicate is not null && workbook.MoveSheet(duplicate,0,out _) && ReferenceEquals(workbook.Sheets[0],duplicate),"sheet reorder");
                Check(duplicate is not null && workbook.RemoveSheet(duplicate,out _) && workbook.Sheets.Count==2,"sheet delete");
                Check(!workbook.RemoveSheet(original,out _) && workbook.Sheets.Count==2,"workbook cannot delete every sheet");
            }
            using(var structure=new WorksheetSession("Structure")) {
                Edit(structure,0,0,"2");Edit(structure,1,0,"=A1*3");Edit(structure,2,1,"=A2+1");
                structure.ApplyFormat([(1,0)],new CellFormat(Bold:true),out _);
                structure.SetRowHeight(1,30);
                Check(structure.InsertRow(0,out _) && structure.GetInput(2,0)=="=A2*3" &&
                    structure.Grid.Rows[2][0].ToString()=="6" && structure.Grid.Rows[3][1].ToString()=="7" &&
                    structure.GetEffectiveFormat(2,0).Bold==true && structure.RowHeights[2]==30,
                    "insert row shifts values, dependents, formats, and dimensions");
                Check(structure.DeleteRow(0,out _) && structure.Grid.Rows[1][0].ToString()=="6" &&
                    structure.Grid.Rows[2][1].ToString()=="7","delete row shifts dependents back");
                Check(structure.InsertColumn(0,out _) && structure.GetInput(2,2)=="=B2+1" &&
                    structure.Grid.Rows[2][1].ToString()=="6" && structure.Grid.Rows[2][2].ToString()=="7",
                    "insert column adjusts references");
                Check(structure.DeleteColumn(0,out _) && structure.Grid.Rows[2][0].ToString()=="6" &&
                    structure.Grid.Rows[2][1].ToString()=="7","delete column restores dependent coordinates");
                Check(!structure.Undo(out _),"structural edit resets incompatible undo history");
            }
            using(var deletedReference=new WorksheetSession("Deleted reference")) {
                Edit(deletedReference,0,0,"4");Edit(deletedReference,1,0,"=A1*2");
                Check(deletedReference.DeleteRow(0,out _) && deletedReference.Grid.Rows[0][0].ToString()=="#REF!",
                    "deleting a referenced row reports a formula error");
            }
            using(var deletedRangeEndpoint=new WorksheetSession("Deleted range endpoint")) {
                Edit(deletedRangeEndpoint,0,0,"1");Edit(deletedRangeEndpoint,1,0,"2");Edit(deletedRangeEndpoint,2,0,"3");
                Edit(deletedRangeEndpoint,4,1,"=SUM(A1:A3)");
                Check(deletedRangeEndpoint.DeleteRow(0,out _) &&
                    deletedRangeEndpoint.GetInput(3,1)=="=SUM(A1:A2)" &&
                    deletedRangeEndpoint.Grid.Rows[3][1].ToString()=="5","deleting a range endpoint contracts dependent ranges");
            }
            using var s=new WorksheetSession("Mixed العربية");Check(s.EngineAvailable,"native available");
            Edit(s,0,0,"Label");Edit(s,0,1,"Value");Edit(s,1,0,"مرحبا");Edit(s,1,1,"2");
            Edit(s,2,0,"B");Edit(s,2,1,"10");Edit(s,3,0,"C");Edit(s,3,1,"=1/2");
            Edit(s,6,2,"=SUM(B2:B4)");Check(!s.CommitCell(1,1,"=SUM(",out _),"failed edit refused");Check(s.GetInput(1,1)=="2","failed edit preserves input");
            Check(s.SetColumnWidth(0,18.5)&&s.SetRowHeight(1,24),"dimensions accepted");
            Check(!s.SetColumnWidth(0,double.NaN)&&!s.SetRowHeight(1,500),"bad dimensions refused");s.SetFrozenColumns(1);
            var format=new CellFormat(FontFamily:"Consolas",FontSize:14,Bold:true,Italic:true,Foreground:"#123456",Background:"#ABCDEF",
                Alignment:CellTextAlignment.Right,VerticalAlignment:CellVerticalAlignment.Center,WrapText:true,NumberFormat:CellNumberFormat.Currency,
                LeftBorder:new CellBorder("double","#102030"),BottomBorder:new CellBorder("medium","#203040"));
            Check(s.ApplyFormat([(1,1)],format,out _),"format with independent borders");
            Check(!s.ApplyFormat([(1,1)],new CellFormat(Foreground:"invalid"),out _),"invalid RGB rejected atomically");
            Check(s.CreateTable(new CellRange(0,0,3,1),"Sales",false,out _),"semantic table");
            Check(!s.CreateTable(new CellRange(0,0,2,1),"Overlap",false,out _),"overlapping table refused");
            string proofBefore="";Check(s.TryGetProof(out proofBefore,out _),"proof before sort");
            var sorted=s.SortedTableView(s.Tables[0].Id,1,false);
            Check(s.Grid.Rows.IndexOf(sorted[1].Row)==3 && s.Grid.Rows.IndexOf(sorted[2].Row)==1 && s.Grid.Rows.IndexOf(sorted[3].Row)==2,"exact numeric sort view");
            Check(s.TryGetProof(out string proofAfter,out _)&&proofAfter==proofBefore,"sorting preserves native proof and coordinates");
            Edit(s,s.Grid.Rows.IndexOf(sorted[1].Row),1,"=3/4");Check(s.Grid.Rows[6][2].ToString()=="51/4","sorted-row edit updates native DAG");
            foreach(var kind in Enum.GetValues<ChartKind>()) Check(s.AddChart(kind,new CellRange(1,0,3,1),out _),kind+" chart model");
            Check(s.SampleChart(s.Charts[0])[2].Value?.Numerator==3,"chart reads native evaluated rational");
            foreach(var theme in SheetTheme.Presets) {
                s.SetTheme(theme); string file=Path.Combine(dir,theme.Name.Replace(' ','_')+".xlsx");
                Check(s.ExportXlsx(file,out string message),"export "+theme.Name+" "+message);
                using var loaded=new WorksheetSession("Roundtrip");
                Check(loaded.LoadXlsx(file,out message),"import "+theme.Name+" "+message);
                Check(loaded.Theme==theme && loaded.ColumnWidths[0]==18.5 && loaded.RowHeights[1]==24 && loaded.FrozenColumns==1,"theme and dimensions round-trip");
                CellFormat got=loaded.GetEffectiveFormat(1,1);
                Check(got.Bold==true && got.Italic==true && got.Alignment==CellTextAlignment.Right && got.WrapText==true &&
                    got.FontFamily=="Consolas" && got.NumberFormat==CellNumberFormat.Currency && got.LeftBorder?.Style=="double","style round-trip");
                Check(loaded.Tables.Count==1 && loaded.Charts.Count==3,"table and chart relationships round-trip");
                Check(loaded.Grid.Rows[1][0].ToString()=="مرحبا" && loaded.Grid.Rows[6][2].ToString()=="51/4","Unicode and formula recalculation round-trip");
                string again=Path.Combine(dir,"again-"+theme.Name.Replace(' ','_')+".xlsx");Check(loaded.ExportXlsx(again,out _),"re-export imported presentation");
            }
            string good=Path.Combine(dir,"Corporate_Blue.xlsx");
            Check(!s.CommitCell(0,1,"Label",out _),"duplicate table header refused");
            Edit(s,0,1,"Revenue");Check(s.Tables[0].Columns[1]=="Revenue","header edit updates table metadata");
            Check(s.Undo(out _) && s.Tables[0].Columns[1]=="Value","header undo updates table metadata");
            using(var totals = new WorksheetSession("Totals"))
            {
                Edit(totals,0,0,"Category"); Edit(totals,0,1,"Amount");
                Edit(totals,1,0,"A"); Edit(totals,1,1,"=1/3"); Edit(totals,2,0,"B"); Edit(totals,2,1,"=1/6");
                Check(totals.CreateTableWithTotals(new CellRange(0,0,2,1),"TotalsTable",out _),"automatic exact totals");
                Check(totals.Grid.Rows[3][1].ToString()=="1/2","native SUM total");
                Check(totals.Undo(out _) && totals.Tables.Count==0 && totals.GetInput(3,1)=="","table and total undo together");
                Check(totals.Redo(out _) && totals.Tables.Count==1 && totals.Grid.Rows[3][1].ToString()=="1/2","table and total redo together");
                string totalFile=Path.Combine(dir,"totals.xlsx"); Check(totals.ExportXlsx(totalFile,out _),"export totals");
                using var reloaded=new WorksheetSession("Totals"); Check(reloaded.LoadXlsx(totalFile,out _) && reloaded.Tables[0].Totals && reloaded.Grid.Rows[3][1].ToString()=="1/2","import total semantics");
                Check(!totals.CommitCell(3,1,"999",out _),"computed total cannot be overwritten");
                Edit(totals,2,1,"=2/3");Check(totals.Grid.Rows[3][1].ToString()=="1","total recalculates after data edit");
            }
            var attacks=new (string Name,string Part,Func<string,string> Edit)[] {
                ("style index","xl/worksheets/sheet1.xml",x=>x.Replace("s=\"1\"","s=\"999999\"")),
                ("bad RGB","xl/styles.xml",x=>x.Replace("FF123456","GG123456")),
                ("invalid surrogate","xl/sharedStrings.xml",x=>x.Replace("مرحبا","_xD800_")),
                ("wide columns","xl/worksheets/sheet1.xml",x=>x.Replace("max=\"1\"","max=\"16385\"")),
                ("DTD","xl/styles.xml",x=>"<!DOCTYPE styleSheet [<!ENTITY x SYSTEM 'file:///C:/Windows/win.ini'>]>"+x[(x.IndexOf("?>",StringComparison.Ordinal)+2)..]),
                ("depth","xl/styles.xml",_=>"<a>"+string.Concat(Enumerable.Repeat("<a>",70))+string.Concat(Enumerable.Repeat("</a>",71))),
                ("cycle","xl/worksheets/sheet1.xml",x=>x.Replace("SUM(B2:B4)","C7")),
                ("unsupported merge","xl/worksheets/sheet1.xml",x=>x.Replace("</worksheet>","<mergeCells count=\"1\"><mergeCell ref=\"A1:B1\"/></mergeCells></worksheet>")),
                ("cross-sheet chart","xl/charts/chart1.xml",x=>x.Replace("Mixed العربية","Other")),
                ("macro content type","[Content_Types].xml",x=>x.Replace("spreadsheetml.sheet.main","vbaProject")),
                ("invalid alignment","xl/styles.xml",x=>x.Replace("horizontal=\"right\"","horizontal=\"invalid\"")),
                ("missing font table","xl/styles.xml",x=>x.Replace("<fonts ","<fontsX ").Replace("</fonts>","</fontsX>"))
            };
            foreach(var a in attacks) {
                string bad=Path.Combine(dir,a.Name.Replace(' ','_')+".xlsx");Rewrite(good,bad,a.Part,a.Edit);
                bool hadProof=s.TryGetProof(out string beforeProof,out _);
                string before=s.GetInput(1,1);Check(!s.LoadXlsx(bad,out _),"reject "+a.Name);Check(s.GetInput(1,1)==before,"failed import retains workbook "+a.Name);
                Check(hadProof && s.TryGetProof(out string afterProof,out _) && afterProof==beforeProof,"failed import preserves native proof "+a.Name);
            }
            using(var malformed=new MemoryStream(new byte[]{80,75,3,4,0,0,0}))
                Check(OpcPackage.TryOpen(malformed,out _)==SheetStatus.ErrFormat,"malformed ZIP refused");
            using(var bomb=new MemoryStream()) {
                using(var zip=new ZipArchive(bomb,ZipArchiveMode.Create,true)) using(var part=zip.CreateEntry("oversize.xml").Open()) part.Write(new byte[OpcPackage.MaxPartBytes+1]);
                bomb.Position=0;Check(OpcPackage.TryOpen(bomb,out _)==SheetStatus.ErrFormat,"oversize compressed member refused");
            }
            // No numeric path in either parser may turn 0.1 into a binary float.
            Check(NativeSheet.TryCreate((nuint)(8<<20),4096,out NativeSheet? native)==SheetStatus.Ok && native is not null,"formula parity sheet");
            using(native!) {
                native!.SetInt64(0,0,2);native.SetInt64(0,1,3);
                Check(native.TryEvaluate(9,9,new NumberNode(0.1),out _)==SheetStatus.ErrUnsupported,"unannotated float refused");
                Check(FormulaParser.TryParse(new string('(',100)+"1"+new string(')',100),out _)==SheetStatus.ErrLimit,"managed nesting limit");
                Check(FormulaLexer.TryTokenize(new string('1',8193),out _)==SheetStatus.ErrLimit,"managed source limit");
                string[] formulas=["COUNT(A1:A4)","COUNTA(A1:A4)","PRODUCT(A1:A2)","ABS(-1/3)","ROUND(1.005,2)","ROUND(-150,-2)",
                    "CEILING(-3/2)","FLOOR(-3/2)","IF(A1<3,7/9,1/0)","IF(FALSE,1/0,2)","1+2=3","1<>2","2<=2","3>2","3>=4"];
                foreach(string f in formulas) {
                    Check(FormulaParser.TryParse(f,out FormulaNode? ast)==SheetStatus.Ok && ast is not null,"managed parse "+f);
                    Check(native.TryEvaluate(9,9,ast!,out var managed)==SheetStatus.Ok,"managed lower "+f);
                    Check(native.TrySetFormula(9,9,f,out _)==SheetStatus.Ok && native.TryRecalculate(out _)==SheetStatus.Ok,"native parse/recalc "+f);
                    Check(native.TryGetCell(9,9,out var cell)==SheetStatus.Ok && (uint)cell.ErrorCode==managed.Error && cell.Value.Num==managed.Value.Numerator && cell.Value.Den==managed.Value.Denominator,"numeric parity "+f);
                }
            }
            string? previousTestArtifacts=Environment.GetEnvironmentVariable("KHZ_TEST_ARTIFACTS");
            string? previousRecoveryDirectory=Environment.GetEnvironmentVariable("KHZ_RECOVERY_DIRECTORY");
            string recoveryDirectory=Path.Combine(dir,"recovery");
            Environment.SetEnvironmentVariable("KHZ_TEST_ARTIFACTS",dir);
            Environment.SetEnvironmentVariable("KHZ_RECOVERY_DIRECTORY",recoveryDirectory);
            App app=new();app.InitializeComponent();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
            MainWindow window=new();window.Show();window.UpdateLayout();
            DataGridCell errorVisual=new();
            CellVisualFormat.Apply(errorVisual,CellFormat.Empty,"#DIV/0!");
            Check(((SolidColorBrush)errorVisual.Background).Color==(Color)ColorConverter.ConvertFromString("#4A1F24") &&
                errorVisual.FontWeight==FontWeights.Bold,"formula error cells use a distinct visual treatment");
            ListBox tabs=(ListBox)window.FindName("SheetList");
            var unavailableUi=new WorksheetSession("Unavailable UI",false,loadError);
            ((ObservableCollection<WorksheetSession>)tabs.ItemsSource).Add(unavailableUi);
            tabs.SelectedItem=unavailableUi;Pump();
            Border errorBanner=(Border)window.FindName("ErrorBanner");
            Check(errorBanner.Visibility==Visibility.Visible && ((TextBlock)window.FindName("ErrorBannerText")).Text==loadError,"native load failure is shown verbatim");
            Check(!((Button)window.FindName("RecalculateButton")).IsEnabled &&
                !((Button)window.FindName("VerifyButton")).IsEnabled &&
                !((Button)window.FindName("CopyProofButton")).IsEnabled &&
                !((Button)window.FindName("ExportXlsxButton")).IsEnabled &&
                !((Button)window.FindName("InsertRowButton")).IsEnabled &&
                !((Button)window.FindName("DeleteColumnButton")).IsEnabled,"calculation commands disabled without native engine");
            var clickSheet=new WorksheetSession("Click");
            ((ObservableCollection<WorksheetSession>)tabs.ItemsSource).Add(clickSheet);
            tabs.SelectedItem=clickSheet;Pump();
            DataGrid clickGrid=(DataGrid)window.FindName("SheetGrid");
            clickGrid.Columns[0].Width=134.5;window.UpdateLayout();
            clickGrid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left){RoutedEvent=UIElement.PreviewMouseLeftButtonUpEvent});
            Check(clickSheet.ColumnWidths.Count==0,"plain grid click does not persist column widths");
            ((ObservableCollection<WorksheetSession>)tabs.ItemsSource).Add(s);tabs.SelectedItem=s;
            ComboBox themeBox=(ComboBox)window.FindName("ThemeBox");themeBox.SelectedIndex=2;
            DataGrid grid=(DataGrid)window.FindName("SheetGrid");
            grid.CurrentCell=new DataGridCellInfo(s.Grid.DefaultView[1],grid.Columns[1]);
            grid.SelectedCells.Add(grid.CurrentCell);window.UpdateLayout();
            void Click(string title)=>Visuals<Button>(window).Single(b=>b.Content is string text && text==title).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Click("Sort ↑");Pump();
            Check(grid.Columns.Count==s.Grid.Columns.Count && !grid.Columns[1].IsReadOnly,"sort retains editable table descriptors");
            var first=(DataRowView)grid.Items[1];Check(s.Grid.Rows.IndexOf(first.Row)==3,"actual sort button retains coordinates");
            grid.CurrentCell=new DataGridCellInfo(first,grid.Columns[1]);grid.SelectedCells.Clear();grid.SelectedCells.Add(grid.CurrentCell);grid.ScrollIntoView(first,grid.Columns[1]);window.UpdateLayout();
            void EditGrid(string value)
            {
                Pump();window.UpdateLayout();
                var target=Visuals<DataGridCell>(grid).Single(c=>c.Column==grid.Columns[1] && c.DataContext==first);
                target.Focus();Keyboard.Focus(target);
                Check(grid.BeginEdit(),"begin actual grid edit");window.UpdateLayout();
                var cell=Visuals<DataGridCell>(grid).Single(c=>c.Column==grid.Columns[1] && c.DataContext==first);
                ((TextBox)cell.Content).Text=value;
                grid.CommitEdit(DataGridEditingUnit.Cell,true);grid.CommitEdit(DataGridEditingUnit.Row,true);Pump();
            }
            EditGrid("=5/4");Check(s.GetInput(3,1)=="=5/4" && s.GetInput(1,1)=="2","sorted grid edit hits native row");
            EditGrid("=SUM(");Check(s.GetInput(3,1)=="=5/4" && s.Grid.Rows[3][1].ToString()=="5/4","rejected UI edit restores displayed value");
            Check(((Border)window.FindName("ErrorBanner")).Visibility==Visibility.Visible,"rejected UI edit shows a visible error");
            s.ApplyFormat([(200,4)],new CellFormat(Background:"#ABCDEF",Foreground:"#123456",Bold:true),out _);
            DataRowView farRow=grid.Items.Cast<DataRowView>().Single(item=>s.Grid.Rows.IndexOf(item.Row)==200);
            grid.ScrollIntoView(farRow,grid.Columns[4]);Pump();window.UpdateLayout();
            DataGridCell farCell=Visuals<DataGridCell>(grid).Single(cell=>cell.Column==grid.Columns[4] && cell.DataContext==farRow);
            Check(((SolidColorBrush)farCell.Background).Color==(Color)ColorConverter.ConvertFromString("#ABCDEF") &&
                farCell.FontWeight==FontWeights.Bold,"virtualized distant cell applies its format on realization");
            TextBox nameBox=(TextBox)window.FindName("NameBox");nameBox.Text="E201";
            nameBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(nameBox)!,0,Key.Enter){RoutedEvent=Keyboard.KeyDownEvent});
            Pump();
            Check(grid.CurrentCell.Column?.DisplayIndex==4 && grid.CurrentItem is DataRowView jumpRow &&
                s.Grid.Rows.IndexOf(jumpRow.Row)==200,"Name Box jumps to a distant cell");
            Check(((TextBlock)window.FindName("SelectionStatsText")).Text.Contains("Count 0",StringComparison.Ordinal),"selection status reports numeric count");
            Check(Visuals<Button>(window).Where(button=>button.Content is string).Select(button=>(string)button.Content).ToHashSet()
                .IsSupersetOf(new[]{"Insert Row","Delete Row","Insert Column","Delete Column"}) &&
                ((Button)window.FindName("ExportXlsxButton")).ToolTip?.ToString()?.Contains("active sheet only",StringComparison.Ordinal)==true,
                "structural commands are visible and multi-sheet export limitation is disclosed");
            grid.Columns[0].Width=134.5;window.UpdateLayout();
            DataGridColumnHeader resizeHeader=Visuals<DataGridColumnHeader>(grid).Single(header=>header.Column==grid.Columns[0]);
            Thumb resizeGrip=Visuals<Thumb>(resizeHeader).First();
            resizeGrip.RaiseEvent(new DragCompletedEventArgs(10,0,false){RoutedEvent=Thumb.DragCompletedEvent});
            Check(Math.Abs(s.ColumnWidths[0]-18.5)<.01,"column resize captured in XLSX width units");
            DataGridColumnHeader autoFitHeader=Visuals<DataGridColumnHeader>(grid).Single(header=>header.Column==grid.Columns[1]);
            autoFitHeader.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left){RoutedEvent=Control.MouseDoubleClickEvent});
            Pump();
            Check(s.ColumnWidths.ContainsKey(1),"column double-click auto-fit persists only the resized column");
            ((TextBox)window.FindName("RowHeightBox")).Text="30";Click("Row Height");
            Check(s.RowHeights[3]==30,"row height control targets native row");
            TextBox formula=(TextBox)window.FindName("FormulaBox");formula.Text="=IF(A1>2,ROUND(1.005,2),0)";
            window.UpdateLayout();Check(((TextBlock)window.FindName("FormulaSyntax")).Inlines.Count>5,"formula bar highlighting executes");
            Check(Equals(themeBox.SelectedItem,SheetTheme.Presets[2]),"theme switch interaction");
            foreach(var theme in SheetTheme.Presets) {
                themeBox.SelectedItem=theme;Pump();window.UpdateLayout();
                Check(s.Theme==theme && ((SolidColorBrush)window.FindResource("WindowBrush")).Color==(Color)ColorConverter.ConvertFromString(theme.Background),"theme binding "+theme.Name);
                var selectedTheme=(ContentPresenter)themeBox.Template.FindName("SelectionText",themeBox);
                Check(Visuals<TextBlock>(selectedTheme).Any(t=>t.Text==theme.Name),"theme picker displays its name "+theme.Name);
                foreach(var button in Visuals<Button>(window).Where(b=>b.Content is "Apply" or "Verify Proof"))
                {
                    Color foreground=theme.Name=="High Contrast"?Colors.Black:Colors.White;
                    Check(((SolidColorBrush)button.Foreground).Color==foreground && Visuals<TextBlock>(button).All(t=>((SolidColorBrush)t.Foreground).Color==foreground),"accent button text contrast "+button.Content+" "+theme.Name);
                }
                Capture((FrameworkElement)window.Content,Path.Combine(dir,"ui-"+theme.Name.Replace(' ','_')+".png"));
            }
            window.Width=1000;window.Height=650;Pump();window.UpdateLayout();Check(grid.ActualHeight>120,"minimum window keeps grid usable");
            Capture((FrameworkElement)window.Content,Path.Combine(dir,"ui-minimum.png"));
            themeBox.IsDropDownOpen=true;Pump();
            Check(themeBox.ItemContainerGenerator.ContainerFromIndex(0) is ComboBoxItem option && option.ActualHeight>0,"theme dropdown template opens and renders options");
            themeBox.IsDropDownOpen=false;
            foreach(var chart in s.Charts) {
                ChartView view=new(chart,s.SampleChart(chart));view.Measure(new Size(640,360));view.Arrange(new Rect(0,0,640,360));view.UpdateLayout();
                Capture(view,Path.Combine(dir,chart.Kind+".png"));
                AutomationPeer peer=UIElementAutomationPeer.CreatePeerForElement(view);
                Check(peer.GetHelpText().Contains("5/4",StringComparison.Ordinal),chart.Kind+" accessible exact values");
            }
            window.Close();
            string recoveryManifest=Path.Combine(recoveryDirectory,"recovery.json");
            Check(File.Exists(recoveryManifest) && Directory.GetDirectories(recoveryDirectory).Any(path=>Directory.GetFiles(path,"*.xlsx").Length>0),
                "close writes a generation-based XLSX recovery snapshot");
            MainWindow restoredWindow=new();restoredWindow.Show();Pump();
            typeof(MainWindow).GetMethod("RestoreRecoverySnapshot",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!
                .Invoke(restoredWindow,null);
            Pump();
            ListBox restoredTabs=(ListBox)restoredWindow.FindName("SheetList");
            WorksheetSession restoredSheet=((ObservableCollection<WorksheetSession>)restoredTabs.ItemsSource)
                .Single(sheet=>sheet.Name=="Mixed العربية");
            Check(restoredTabs.Items.Count==4 && restoredSheet.GetInput(3,1)=="=5/4" &&
                restoredSheet.GetEffectiveFormat(200,4).Background=="#ABCDEF","recovery restores sheet inputs and presentation");
            restoredWindow.Close();
            Environment.SetEnvironmentVariable("KHZ_TEST_ARTIFACTS",previousTestArtifacts);
            Environment.SetEnvironmentVariable("KHZ_RECOVERY_DIRECTORY",previousRecoveryDirectory);
            app.Shutdown();
            Console.WriteLine($"checks={checks} failures=0");return 0;
        } catch(Exception ex) {Console.Error.WriteLine(ex);Console.WriteLine($"checks={checks} failures=1");return 1;}
        finally {if(artifacts is null) Directory.Delete(dir,true);}
    }
}
