using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using KHZ.Sheet.Core;
using KHZ.Sheet.Desktop;
using CellRange=KHZ.Sheet.Desktop.CellRange;

internal static class Program
{
    private static int failures;
    private static void Check(bool ok,string name){Console.WriteLine((ok?"PASS ":"FAIL ")+name);if(!ok)failures++;}
    private static void Edit(WorksheetSession s,int row,int col,string value){Check(s.CommitCell(row,col,value,out _),$"edit {row},{col}");}
    private static void Mutate(string source,string target,string entry,Func<string,string> edit)
    {
        File.Copy(source,target,true);
        using var z=ZipFile.Open(target,ZipArchiveMode.Update);
        var old=z.GetEntry(entry) ?? throw new InvalidOperationException(entry);
        string text;using(var r=new StreamReader(old.Open()))text=r.ReadToEnd();
        old.Delete();var e=z.CreateEntry(entry,CompressionLevel.NoCompression);using var w=new StreamWriter(e.Open());w.Write(edit(text));
    }
    public static int Main()
    {
        string dir=Environment.GetEnvironmentVariable("KHZ_TEST_ARTIFACTS") ?? Path.Combine(Path.GetTempPath(),"khz-subsystem-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var s=new WorksheetSession("Mixed العربية");
            Check(s.EngineAvailable,"native engine available");
            Edit(s,0,0,"Item");Edit(s,0,1,"Value");Edit(s,1,0,"مرحبا");Edit(s,1,1,"1/3");Edit(s,2,0,"B");Edit(s,2,1,"1/6");Edit(s,3,0,"C");Edit(s,3,1,"2");
            Edit(s,5,0,"Style");Edit(s,5,1,"7");Edit(s,6,0,"Total");Edit(s,6,1,"=SUM(B2:B4)");Edit(s,6,2,"=A2&B2");
            Check(!s.CommitCell(6,2,"=SUM(B2:B4)",out _),"unsupported formula refused without mutation");
            Check(!s.SetColumnWidth(0,double.NaN)&&!s.SetRowHeight(1,500),"bad dimensions refused");s.SetFrozenColumns(1);
            Check(s.ApplyFormat([(1,1)],new CellFormat(FontFamily:"Consolas",FontSize:14,Bold:true,Foreground:"#123456",Background:"#ABCDEF",Alignment:CellTextAlignment.Right,VerticalAlignment:CellVerticalAlignment.Center,WrapText:true,NumberFormat:CellNumberFormat.Currency,LeftBorder:new CellBorder("double","#010203")),out _),"explicit style accepted");
            s.ApplyTableFormat(new CellRange(0,0,3,1),out _);Check(s.CreateTable(new CellRange(0,0,3,1),"Numbers",false,out _),"semantic table accepted");
            s.AddChart(ChartKind.Column,new CellRange(0,0,3,1),out _);s.AddChart(ChartKind.Bar,new CellRange(0,0,3,1),out _);s.AddChart(ChartKind.Line,new CellRange(0,0,3,1),out _);
            int themes=0;
            foreach(var theme in SheetTheme.Presets)
            {
                s.SetTheme(theme);s.SetColumnWidth(0,18.5);s.SetRowHeight(1,24);
                string file=Path.Combine(dir,theme.Name.Replace(' ','_')+".xlsx");
                Check(s.ExportXlsx(file,out string msg),"export "+theme.Name+" "+msg);
                using var loaded=new WorksheetSession("Imported");Check(loaded.LoadXlsx(file,out msg),"import "+theme.Name+" "+msg);
                Check(loaded.Theme==theme && loaded.ColumnWidths[0]==18.5 && loaded.RowHeights[1]==24 && loaded.FrozenColumns==1,"theme and dimensions round-trip");
                var got=loaded.GetEffectiveFormat(1,1);Check(got.Foreground=="#123456" && got.Background=="#ABCDEF" && got.FontFamily=="Consolas" && got.NumberFormat==CellNumberFormat.Currency && got.LeftBorder?.Style=="double","style round-trip");
                Check(loaded.Tables.Count==1 && loaded.Charts.Count==3,"table and chart relationships round-trip");
                Check(loaded.Grid.Rows[1][0].ToString()=="مرحبا" && loaded.Grid.Rows[6][2].ToString()=="51/4","Unicode and formula recalculation round-trip");
                string again=Path.Combine(dir,"again-"+theme.Name.Replace(' ','_')+".xlsx");Check(loaded.ExportXlsx(again,out _),"re-export imported presentation");
                themes++;
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
                string optional=Path.Combine(dir,$"panes-optional-{columns}-{rows}.xlsx");
                Mutate(path,optional,"xl/worksheets/sheet1.xml",x=>
                    Regex.Replace(Regex.Replace(x," topLeftCell=\"[^\"]+\"","")," activePane=\"[^\"]+\"",""));
                using var optionalLoaded=new WorksheetSession("Optional");
                Check(optionalLoaded.LoadXlsx(optional,out string optionalMessage) &&
                    optionalLoaded.FrozenColumns==columns && optionalLoaded.FrozenRows==rows,
                    "optional frozen pane metadata "+optionalMessage);
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
            foreach(var attack in attacks)
            {
                string bad=Path.Combine(dir,"bad-"+Regex.Replace(attack.Name,"[^a-z]","-",RegexOptions.IgnoreCase)+".xlsx");Mutate(good,bad,attack.Part,attack.Edit);
                using var staged=new WorksheetSession("Staged");Edit(staged,0,0,"keep");
                Check(!staged.LoadXlsx(bad,out _),attack.Name+" rejected");Check(staged.GetInput(0,0)=="keep" && staged.NativeCellCount==1,"rejected import is atomic");
            }
            using(var native=NativeSheet.Create(32*1024*1024,100000))
            {
                string[] formulas={"=AND(TRUE,1=1)","=OR(FALSE,2>1)","=NOT(FALSE)","=IFERROR(1/0,2/7)"};
                foreach(string formula in formulas)
                {
                    Check(FormulaParser.TryParse(formula,out FormulaNode? ast)==SheetStatus.Ok && ast is not null,"managed parse "+formula);
                    Check(native.TryEvaluate(9,9,ast!,out var managed)==SheetStatus.Ok && native.TrySetFormula(9,9,formula,out _)==SheetStatus.Ok,
                        "managed/native install "+formula);
                    Check(native.TryRecalculate(out _)==SheetStatus.Ok && native.TryGetCell(9,9,out var cell)==SheetStatus.Ok && cell.Value.Equals(managed),
                        "managed/native exact parity "+formula);
                }
                string[] malformed={"=AND()","=NOT(1,2)","=IFERROR(1)","=IFERROR(1,2,3)"};
                foreach(string f in malformed)
                {
                    Check(FormulaParser.TryParse(f,out FormulaNode? ast)==SheetStatus.Ok && ast is not null,"managed parse malformed call "+f);
                    Check(native.TryEvaluate(9,9,ast!,out _)==SheetStatus.ErrFormat &&
                        native.TrySetFormula(9,9,f,out _)==SheetStatus.ErrFormat,"managed and native reject malformed call "+f);
                }
            }
            using(var workbook=new WorkbookSession())
            {
                WorksheetSession original=workbook.Sheets[0];
                Check(WorkbookSession.IsValidSheetName("Sheet 1") &&
                    !WorkbookSession.IsValidSheetName("") && !WorkbookSession.IsValidSheetName(new string('x',32)) &&
                    !WorkbookSession.IsValidSheetName("bad/name") && !WorkbookSession.IsValidSheetName("'bad") &&
                    !WorkbookSession.IsValidSheetName("bad'") && !WorkbookSession.IsValidSheetName("bad*name") &&
                    !WorkbookSession.IsValidSheetName("bad[name") && !WorkbookSession.IsValidSheetName("   "),
                    "Excel worksheet name rules");
                Check(!workbook.RenameSheet(original,"bad/name",out _),"invalid rename refused");
                bool rejectedInvalidAdd=false;
                try { workbook.AddSheet("bad:name"); } catch(ArgumentException) { rejectedInvalidAdd=true; }
                Check(rejectedInvalidAdd,"invalid add name refused");
                WorksheetSession other=workbook.AddSheet("Other");
                Check(!workbook.RenameSheet(original,"oTHER",out _),"case-insensitive duplicate rename refused");
                Check(workbook.RenameSheet(other,"Revenue",out _),"rename accepted");
                WorksheetSession duplicateName=workbook.AddSheet("Revenue");
                Check(duplicateName.Name=="Revenue 2","duplicate add name made unique");
                Check(workbook.RenameSheet(original,new string('S',31),out _),"31-character worksheet name accepted");
                Check(workbook.MoveSheet(duplicateName,-1,out _) && workbook.Sheets.IndexOf(duplicateName)==1,"move worksheet left");
                Check(workbook.MoveSheet(duplicateName,1,out _) && workbook.Sheets.IndexOf(duplicateName)==2,"move worksheet right");
                Check(!workbook.MoveSheet(original,-1,out _),"cannot move first worksheet left");
                Check(workbook.DeleteSheet(duplicateName,out _),"delete worksheet");
                Check(workbook.DeleteSheet(other,out _),"delete until one worksheet");
                Check(!workbook.DeleteSheet(original,out _),"final worksheet cannot be deleted");
            }
            using(var workbook=new WorkbookSession())
            {
                WorksheetSession original=workbook.Sheets[0];
                Edit(original,0,0,"Header");Edit(original,1,0,"1/3");Edit(original,1,1,"=A2*3");
                original.ApplyFormat([(1,1)],new CellFormat(Foreground:"#123456",Bold:true),out _);
                original.ApplyTableFormat(new CellRange(0,0,1,1),out _);
                Check(original.CreateTable(new CellRange(0,0,1,1),"CloneTable",false,out _),"clone source table");
                Check(original.AddChart(ChartKind.Line,new CellRange(0,0,1,1),out _),"clone source chart");
                original.SetColumnWidth(1,22);original.SetRowHeight(1,28);original.SetFrozenColumns(1); original.SetFrozenRows(2);
                original.SetTheme(SheetTheme.Presets[1]);
                Check(workbook.DuplicateSheet(original,null,out var copy,out string cloneMessage),"duplicate worksheet "+cloneMessage);
                Check(copy is not null && copy.Name=="Sheet1 Copy" && copy.GetInput(1,1)=="=A2*3" && copy.Grid.Rows[1][1].ToString()=="1" &&
                    copy.GetEffectiveFormat(1,1).Foreground=="#123456" && copy.ColumnWidths[1]==22 && copy.RowHeights[1]==28 &&
                    copy.Tables.Count==1 && copy.Charts.Count==1 && copy.Theme==SheetTheme.Presets[1] &&
                    copy.FrozenColumns==1 && copy.FrozenRows==2,"duplicate copies values and modeled worksheet state");
                var clone=copy!;
                Check(original.Tables[0].Id!=clone.Tables[0].Id && original.Charts[0].Id!=clone.Charts[0].Id,"duplicate assigns independent object identities");
                Edit(clone,1,0,"2/3");clone.ApplyFormat([(1,1)],new CellFormat(Foreground:"#654321"),out _);
                clone.SetFrozenRows(3);clone.SetRowHeight(2,30);clone.SetTheme(SheetTheme.Presets[0]);
                Check(original.GetEffectiveFormat(1,1).Foreground=="#123456" && original.FrozenRows==2 &&
                    !original.RowHeights.ContainsKey(2) && original.Theme==SheetTheme.Presets[1] && original.Grid.Rows[1][1].ToString()=="1",
                    "duplicate state is independent");
                Check(!workbook.DuplicateSheet(original,"sheet1 copy",out _,out _),"duplicate name uniqueness is case-insensitive");
            }
            Console.WriteLine($"themes={themes} failures={failures} artifacts={dir}");return failures==0?0:1;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 2;}
    }
}
