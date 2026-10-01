using System.Data;
using System.Collections.ObjectModel;
using System.Windows.Automation.Peers;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
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
            foreach(var (columns,rows,topLeft,activePane) in new[] {
                (0,2,"A3","bottomLeft"),(1,0,"B1","topRight"),(1,2,"B3","bottomRight") })
            {
                using var panes=new WorksheetSession("Panes");
                panes.SetFrozenColumns(columns);panes.SetFrozenRows(rows);
                string path=Path.Combine(dir,$"panes-{columns}-{rows}.xlsx");
                Check(panes.ExportXlsx(path,out string exportMessage),"export frozen pane "+exportMessage);
                using(var archive=ZipFile.OpenRead(path))
                using(var xml=archive.GetEntry("xl/worksheets/sheet1.xml")!.Open())
                {
                    XElement pane=XDocument.Load(xml).Descendants(XName.Get("pane","http://schemas.openxmlformats.org/spreadsheetml/2006/main")).Single();
                    Check((string?)pane.Attribute("xSplit")==columns.ToString() &&
                        (string?)pane.Attribute("ySplit")==rows.ToString() &&
                        (string?)pane.Attribute("topLeftCell")==topLeft &&
                        (string?)pane.Attribute("activePane")==activePane,"frozen pane XML coordinates");
                }
                using var loaded=new WorksheetSession("Imported");
                Check(loaded.LoadXlsx(path,out string importMessage) &&
                    loaded.FrozenColumns==columns && loaded.FrozenRows==rows,
                    "frozen pane round trip "+importMessage);
            }
            using(var paneLimits=new WorksheetSession("PaneLimits"))
            {
                bool columnsAtLimit=false,rowsAtLimit=false,columnsOverLimit=false,rowsOverLimit=false;
                try { paneLimits.SetFrozenColumns(paneLimits.Grid.Columns.Count); } catch(ArgumentOutOfRangeException) { columnsAtLimit=true; }
                try { paneLimits.SetFrozenRows(paneLimits.Grid.Rows.Count); } catch(ArgumentOutOfRangeException) { rowsAtLimit=true; }
                try { paneLimits.SetFrozenColumns(paneLimits.Grid.Columns.Count+1); } catch(ArgumentOutOfRangeException) { columnsOverLimit=true; }
                try { paneLimits.SetFrozenRows(paneLimits.Grid.Rows.Count+1); } catch(ArgumentOutOfRangeException) { rowsOverLimit=true; }
                Check(columnsAtLimit&&rowsAtLimit&&columnsOverLimit&&rowsOverLimit,"pane setters reject exact and over-limit splits");
                string source=Path.Combine(dir,"panes-1-0.xlsx");
                void RejectPane(string suffix,Func<string,string> edit)
                {
                    string path=Path.Combine(dir,"bad-pane-"+suffix+".xlsx");
                    Rewrite(source,path,"xl/worksheets/sheet1.xml",edit);
                    using var invalid=new WorksheetSession("InvalidPane");
                    bool rejected;
                    try { rejected=!invalid.LoadXlsx(path,out _); } catch { rejected=false; }
                    Check(rejected,"pane import safely rejects "+suffix);
                }
                RejectPane("columns-at-limit",x=>x.Replace("xSplit=\"1\"","xSplit=\"52\""));
                RejectPane("columns-over-limit",x=>x.Replace("xSplit=\"1\"","xSplit=\"53\""));
                RejectPane("rows-at-limit",x=>x.Replace("ySplit=\"0\"","ySplit=\"256\""));
                RejectPane("rows-over-limit",x=>x.Replace("ySplit=\"0\"","ySplit=\"257\""));
                string metadataPath=Path.Combine(dir,"pane-metadata.xlsx");
                Rewrite(source,metadataPath,"xl/worksheets/sheet1.xml",
                    x=>x.Replace("topLeftCell=\"B1\"","topLeftCell=\"A1\"")
                        .Replace("activePane=\"topRight\"","activePane=\"bottomRight\""));
                using(var metadata=new WorksheetSession("Metadata"))
                    Check(metadata.LoadXlsx(metadataPath,out _) && metadata.FrozenColumns==1,
                        "safe split ignores differing optional pane metadata");
                string omittedPath=Path.Combine(dir,"pane-metadata-omitted.xlsx");
                Rewrite(source,omittedPath,"xl/worksheets/sheet1.xml",
                    x=>x.Replace(" topLeftCell=\"B1\"","").Replace(" activePane=\"topRight\"",""));
                using(var omitted=new WorksheetSession("OmittedMetadata"))
                    Check(omitted.LoadXlsx(omittedPath,out _) && omitted.FrozenColumns==1,
                        "safe split accepts omitted optional pane metadata");
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
                ("frozen pane coordinates","xl/worksheets/sheet1.xml",x=>x.Replace("topLeftCell=\"B1\"","topLeftCell=\"A1\"")),
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
                    "CEILING(-3/2)","FLOOR(-3/2)","IF(A1<3,7/9,1/0)","IF(FALSE,1/0,2)",
                    "AND(TRUE,1=1)","OR(FALSE,1=1)","NOT(FALSE)","IFERROR(1/0,2/7)","IFERROR(1,1/0)",
                    "1+2=3","1<>2","2<=2","3>2","3>=4"];
                foreach(string f in formulas) {
                    Check(FormulaParser.TryParse(f,out FormulaNode? ast)==SheetStatus.Ok && ast is not null,"managed parse "+f);
                    Check(native.TryEvaluate(9,9,ast!,out var managed)==SheetStatus.Ok,"managed lower "+f);
                    Check(native.TrySetFormula(9,9,f,out _)==SheetStatus.Ok && native.TryRecalculate(out _)==SheetStatus.Ok,"native parse/recalc "+f);
                    Check(native.TryGetCell(9,9,out var cell)==SheetStatus.Ok && (uint)cell.ErrorCode==managed.Error && cell.Value.Num==managed.Value.Numerator && cell.Value.Den==managed.Value.Denominator,"numeric parity "+f);
                }
                foreach(string f in new[] {"AND()","NOT(1,2)","IFERROR(1)","IFERROR(1,2,3)"}) {
                    Check(FormulaParser.TryParse(f,out FormulaNode? ast)==SheetStatus.Ok && ast is not null,"managed parse malformed call "+f);
                    Check(native.TryEvaluate(9,9,ast!,out _)==SheetStatus.ErrFormat &&
                        native.TrySetFormula(9,9,f,out _)==SheetStatus.ErrFormat,"managed and native reject malformed call "+f);
                }
            }
            using(var workbook=new WorkbookSession())
            {
                WorksheetSession original=workbook.Sheets[0];
                Check(WorkbookSession.IsValidSheetName("Sheet 1") &&
                    WorkbookSession.IsValidSheetName("x") && WorkbookSession.IsValidSheetName(new string('x',31)) &&
                    !WorkbookSession.IsValidSheetName("") && !WorkbookSession.IsValidSheetName("   ") &&
                    !WorkbookSession.IsValidSheetName(new string('x',32)) &&
                    !WorkbookSession.IsValidSheetName("bad/name") && !WorkbookSession.IsValidSheetName("'bad") &&
                    !WorkbookSession.IsValidSheetName("bad'") && !WorkbookSession.IsValidSheetName("bad*name") &&
                    !WorkbookSession.IsValidSheetName("bad[name"),"Excel worksheet name rules");
                Check(workbook.RenameSheet(original,"  Normalized  ",out _) && original.Name=="Normalized",
                    "rename trims surrounding whitespace");
                Check(!workbook.RenameSheet(original,"bad/name",out _),"invalid rename refused");
                bool rejectedInvalidAdd=false;
                try { workbook.AddSheet("bad:name"); } catch(ArgumentException) { rejectedInvalidAdd=true; }
                Check(rejectedInvalidAdd,"invalid add name refused");
                bool rejectedBlankAdd=false;
                try { workbook.AddSheet("   "); } catch(ArgumentException) { rejectedBlankAdd=true; }
                Check(rejectedBlankAdd,"whitespace-only add name refused");
                WorksheetSession trimmedAdd=workbook.AddSheet("  Trimmed  ");
                Check(trimmedAdd.Name=="Trimmed","add trims surrounding whitespace");
                WorksheetSession other=workbook.AddSheet("Other");
                Check(!workbook.RenameSheet(original,"oTHER",out _),"case-insensitive duplicate rename refused");
                Check(workbook.RenameSheet(other,"Revenue",out _),"rename accepted");
                Check(workbook.RenameSheet(other,"   ",out _)==false,"whitespace-only rename refused");
                WorksheetSession duplicateName=workbook.AddSheet("Revenue");
                Check(duplicateName.Name=="Revenue 2","duplicate add name made unique");
                Check(workbook.DuplicateSheet(original,"  Named Copy  ",out WorksheetSession? namedCopy,out _) &&
                    namedCopy?.Name=="Named Copy","duplicate trims surrounding whitespace");
                Check(workbook.RenameSheet(original,new string('S',31),out _),"31-character worksheet name accepted");
                Edit(original,0,0,"Category"); Edit(original,0,1,"Amount");
                Edit(original,1,0,"Synthetic-A"); Edit(original,1,1,"=1/2");
                Edit(original,2,0,"Synthetic-B"); Edit(original,2,1,"=1/3");
                Edit(original,1,2,"=IFERROR(1/B2,AND(FALSE,NOT(FALSE)))");
                Check(original.Grid.Rows[1][2].ToString()=="2","managed logical/error formula result");
                Edit(original,1,1,"0");
                Check(original.Grid.Rows[1][2].ToString()=="0","managed logical/error dependency recalculation");
                Edit(original,1,1,"=1/2");
                Check(original.ApplyFormat([(1,1)],new CellFormat(Bold:true,Foreground:"#123456"),out _),"source explicit format");
                Check(original.ApplyTableFormat(new CellRange(0,0,2,1),out _),"source table format");
                Check(original.CreateTable(new CellRange(0,0,2,1),"SyntheticTable",false,out _),"source table metadata");
                Check(original.AddChart(ChartKind.Line,new CellRange(0,0,2,1),out _),"source chart metadata");
                original.SetTheme(SheetTheme.Presets[3]);
                Check(original.SetColumnWidth(1,19)&&original.SetRowHeight(2,27),"source dimensions");
                original.SetFrozenColumns(1); original.SetFrozenRows(2);
                Check(workbook.DuplicateSheet(original,null,out WorksheetSession? copy,out string copyMessage),
                    "deep duplicate "+copyMessage);
                Check(copy is not null && copy.Name.Length<=31 && copy.Name.EndsWith(" Copy",StringComparison.Ordinal) &&
                    copy.GetInput(1,1)=="=1/2" &&
                    copy.GetInput(1,2)=="=IFERROR(1/B2,AND(FALSE,NOT(FALSE)))" &&
                    copy.Grid.Rows[1][1].ToString()=="1/2" && copy.Grid.Rows[1][2].ToString()=="2" &&
                    copy.GetEffectiveFormat(1,1).Bold==true && copy.Tables.Count==1 && copy.Charts.Count==1 &&
                    copy.Tables[0].Id!=original.Tables[0].Id && copy.Charts[0].Id!=original.Charts[0].Id &&
                    copy.Theme==original.Theme && copy.ColumnWidths[1]==19 && copy.RowHeights[2]==27 &&
                    copy.FrozenColumns==1 && copy.FrozenRows==2,"duplicate copies values and modeled worksheet state");
                WorksheetSession clone=copy!;
                clone.ApplyFormat([(1,1)],new CellFormat(Foreground:"#654321"),out _);
                clone.SetFrozenRows(3);clone.SetRowHeight(2,30);clone.SetTheme(SheetTheme.Presets[0]);
                Check(original.GetEffectiveFormat(1,1).Foreground=="#123456" && original.FrozenRows==2 &&
                    original.RowHeights[2]==27 && original.Theme==SheetTheme.Presets[3],"duplicate presentation state is independent");
                Check(clone.CommitCell(0,0,"CopyLabel",out _) && original.GetInput(0,0)=="Category" &&
                    clone.Tables[0].Columns[0]=="CopyLabel" && original.Tables[0].Columns[0]=="Category",
                    "duplicate cell and table metadata are independent");
                Check(clone.AddChart(ChartKind.Column,new CellRange(0,0,2,1),out _) && clone.Charts.Count==2 &&
                    original.Charts.Count==1,"duplicate chart collection is independent");
                Check(clone.CommitCell(1,1,"2",out _) && original.GetInput(1,1)=="=1/2" &&
                    clone.Grid.Rows[1][1].ToString()=="2" && clone.Grid.Rows[1][2].ToString()=="1/2" &&
                    original.Grid.Rows[1][2].ToString()=="2","duplicate formulas and dependencies are independent");
                Check(workbook.MoveSheet(clone,1,out _) && workbook.Sheets.IndexOf(clone)==2 &&
                    workbook.MoveSheet(clone,-1,out _) && workbook.Sheets.IndexOf(clone)==1,
                    "move worksheet left and right");
                Check(!workbook.MoveSheet(original,-1,out _) && workbook.Sheets.Count==6,
                    "worksheet move boundary");
                Check(workbook.DeleteSheet(clone,out _) && !workbook.Sheets.Contains(clone),"delete worksheet");
                while(workbook.Sheets.Count>1) Check(workbook.DeleteSheet(workbook.Sheets[1],out _),"delete non-final worksheet");
                Check(!workbook.DeleteSheet(original,out _) && workbook.Sheets.Single()==original,"final worksheet cannot be deleted");
            }
            App app=new();app.InitializeComponent();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
            MainWindow window=new();window.Show();window.UpdateLayout();
            ListBox tabs=(ListBox)window.FindName("SheetList");
            ((ObservableCollection<WorksheetSession>)tabs.ItemsSource).Add(s);tabs.SelectedItem=s;
            window.UpdateLayout();
            TabControl ribbon=(TabControl)window.FindName("RibbonTabs");
            void ShowTab(string header)=>ribbon.SelectedItem=ribbon.Items.Cast<TabItem>().Single(t=>(string)t.Header==header);
            Check(ribbon.Items.Cast<TabItem>().Select(t=>(string)t.Header)
                .SequenceEqual(new[] {"Home","Insert / Data","View"}),"ribbon command groups are available");
            var tabContainer=(ListBoxItem)tabs.ItemContainerGenerator.ContainerFromItem(s);
            Check(tabContainer.ContextMenu?.Items.OfType<MenuItem>().Select(m=>(string)m.Header)
                .SequenceEqual(new[] {"Rename…","Duplicate","Move Left","Move Right","Delete"})==true,
                "worksheet tabs expose lifecycle context actions");
            ComboBox themeBox=(ComboBox)window.FindName("ThemeBox");themeBox.SelectedIndex=2;
            DataGrid grid=(DataGrid)window.FindName("SheetGrid");
            grid.CurrentCell=new DataGridCellInfo(s.Grid.DefaultView[1],grid.Columns[1]);
            grid.SelectedCells.Add(grid.CurrentCell);window.UpdateLayout();
            void Click(string title)=>Visuals<Button>(window).Single(b=>b.Content is string text && text==title).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ShowTab("Insert / Data");Click("Sort ↑");Pump();
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
            grid.Columns[0].Width=134.5;window.UpdateLayout();
            grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left){RoutedEvent=UIElement.PreviewMouseLeftButtonUpEvent});
            Check(Math.Abs(s.ColumnWidths[0]-18.5)<.01,"column resize captured in XLSX width units");
            ShowTab("View");((TextBox)window.FindName("RowHeightBox")).Text="30";Click("Row Height");
            Check(s.RowHeights[3]==30,"row height control targets native row");
            TextBox formula=(TextBox)window.FindName("FormulaBox");formula.Text="=IF(A1>2,ROUND(1.005,2),0)";
            window.UpdateLayout();Check(((TextBlock)window.FindName("FormulaSyntax")).Inlines.Count>5,"formula bar highlighting executes");
            Check(Equals(themeBox.SelectedItem,SheetTheme.Presets[2]),"theme switch interaction");
            ShowTab("View");
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
            window.Close();app.Shutdown();
            Console.WriteLine($"checks={checks} failures=0");return 0;
        } catch(Exception ex) {Console.Error.WriteLine(ex);Console.WriteLine($"checks={checks} failures=1");return 1;}
        finally {if(artifacts is null) Directory.Delete(dir,true);}
    }
}
