using System.Globalization;
using System.Xml.Linq;
using KHZ.Sheet.Core;

namespace KHZ.Sheet.Desktop;

internal static partial class XlsxPresentationSerializer
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace C = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace D = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
    private static void Override(XDocument types,string path,string type)
    {
        XNamespace ns=types.Root!.Name.Namespace;
        if(types.Root.Elements(ns+"Override").Any(e=>(string?)e.Attribute("PartName")=="/"+path)) return;
        types.Root.Add(new XElement(ns+"Override",new XAttribute("PartName","/"+path),new XAttribute("ContentType",type)));
    }
    private static XElement Rel(string id,string type,string target) => new(P+"Relationship",
        new XAttribute("Id",id),new XAttribute("Type",R.NamespaceName+"/"+type),new XAttribute("Target",target));
    private static void ApplyLayout(XDocument sheet,WorksheetSession session)
    {
        var root=sheet.Root!;
        XElement data=root.Element(S+"sheetData")!;
        if(session.ColumnWidths.Count>0)
            data.AddBeforeSelf(new XElement(S+"cols",session.ColumnWidths.OrderBy(p=>p.Key).Select(p=>
                new XElement(S+"col",new XAttribute("min",p.Key+1),new XAttribute("max",p.Key+1),
                    new XAttribute("width",p.Value.ToString("R",CultureInfo.InvariantCulture)),new XAttribute("customWidth",1)))));
        var rows=data.Elements(S+"row").ToDictionary(e=>(int)e.Attribute("r")!);
        foreach(var pair in session.RowHeights.OrderBy(p=>p.Key))
        {
            if(!rows.TryGetValue(pair.Key+1,out var row)) {row=new XElement(S+"row",new XAttribute("r",pair.Key+1));rows.Add(pair.Key+1,row);data.Add(row);}
            row.SetAttributeValue("ht",pair.Value.ToString("R",CultureInfo.InvariantCulture));row.SetAttributeValue("customHeight",1);
        }
        data.ReplaceNodes(rows.OrderBy(p=>p.Key).Select(p=>p.Value));
        if(session.FrozenColumns>0)
        {
            root.Element(S+"sheetViews")?.Remove();
            var views=new XElement(S+"sheetViews",new XElement(S+"sheetView",new XAttribute("workbookViewId",0),
                new XElement(S+"pane",new XAttribute("xSplit",session.FrozenColumns),new XAttribute("topLeftCell",WorksheetSession.ColumnName(session.FrozenColumns)+"1"),
                    new XAttribute("activePane","topRight"),new XAttribute("state","frozen"))));
            var dimension=root.Element(S+"dimension");
            if(dimension is null) root.AddFirst(views); else dimension.AddAfterSelf(views);
        }
    }
    private static void ApplyObjects(OpcPackage package,XDocument sheet,XDocument types,WorksheetSession session)
    {
        XElement rels=new(P+"Relationships");
        if(session.Tables.Count>0)
        {
            XElement parts=new(S+"tableParts",new XAttribute("count",session.Tables.Count));
            int id=0;
            foreach(var table in session.Tables)
            {
                ++id; string path=$"xl/tables/table{id}.xml";
                XElement columns=new(S+"tableColumns",new XAttribute("count",table.Columns.Count));
                for(int i=0;i<table.Columns.Count;++i) {
                    XElement col=new(S+"tableColumn",new XAttribute("id",i+1),new XAttribute("name",table.Columns[i]));
                    if(table.Totals)
                    {
                        if(i == 0 && table.Columns.Count > 1 && session.Grid.Rows[table.Range.BottomRight.Row][table.Range.TopLeft.Column]?.ToString() == "Total")
                            col.Add(new XAttribute("totalsRowLabel","Total"));
                        else col.Add(new XAttribute("totalsRowFunction","sum"));
                    }
                    columns.Add(col);
                }
                var end=table.Range.BottomRight;
                string filter=$"{table.Range.TopLeft}:{WorksheetSession.ColumnName(end.Column)}{end.Row+1-(table.Totals?1:0)}";
                XDocument doc=new(new XElement(S+"table",new XAttribute("id",id),new XAttribute("name",table.Name),new XAttribute("displayName",table.Name),
                    new XAttribute("ref",table.Range.ToString()),new XAttribute("totalsRowCount",table.Totals?1:0),
                    new XElement(S+"autoFilter",new XAttribute("ref",filter)),columns,
                    new XElement(S+"tableStyleInfo",new XAttribute("name","TableStyleMedium2"),new XAttribute("showFirstColumn",0),
                        new XAttribute("showLastColumn",0),new XAttribute("showRowStripes",1),new XAttribute("showColumnStripes",0))));
                BoundedXml.Put(package,path,doc);
                Override(types,path,"application/vnd.openxmlformats-officedocument.spreadsheetml.table+xml");
                rels.Add(Rel($"khzTable{id}","table",$"../tables/table{id}.xml"));
                parts.Add(new XElement(S+"tablePart",new XAttribute(R+"id",$"khzTable{id}")));
            }
            sheet.Root!.Add(parts);
        }
        if(session.Charts.Count>0)
        {
            XElement drawing=new(D+"wsDr"); XElement drawingRels=new(P+"Relationships"); int id=0;
            foreach(var chart in session.Charts)
            {
                ++id; string path=$"xl/charts/chart{id}.xml";
                BoundedXml.Put(package,path,ChartXml(chart,session.SampleChart(chart),session.Name));
                Override(types,path,"application/vnd.openxmlformats-officedocument.drawingml.chart+xml");
                drawingRels.Add(Rel($"chart{id}","chart",$"../charts/chart{id}.xml"));
                drawing.Add(new XElement(D+"twoCellAnchor",Marker("from",3,(id-1)*16),Marker("to",12,(id-1)*16+15),
                    new XElement(D+"graphicFrame",new XAttribute("macro",""),
                        new XElement(D+"nvGraphicFramePr",new XElement(D+"cNvPr",new XAttribute("id",id+1),new XAttribute("name",chart.Title)),new XElement(D+"cNvGraphicFramePr")),
                        new XElement(D+"xfrm",new XElement(A+"off",new XAttribute("x",0),new XAttribute("y",0)),new XElement(A+"ext",new XAttribute("cx",0),new XAttribute("cy",0))),
                        new XElement(A+"graphic",new XElement(A+"graphicData",new XAttribute("uri",C.NamespaceName),new XElement(C+"chart",new XAttribute(R+"id",$"chart{id}"))))),
                    new XElement(D+"clientData")));
            }
            BoundedXml.Put(package,"xl/drawings/drawing1.xml",new XDocument(drawing));
            BoundedXml.Put(package,"xl/drawings/_rels/drawing1.xml.rels",new XDocument(drawingRels));
            Override(types,"xl/drawings/drawing1.xml","application/vnd.openxmlformats-officedocument.drawing+xml");
            rels.Add(Rel("khzDrawing","drawing","../drawings/drawing1.xml"));
            // drawing precedes tableParts in the worksheet sequence.
            var draw=new XElement(S+"drawing",new XAttribute(R+"id","khzDrawing"));
            var tableParts=sheet.Root!.Element(S+"tableParts"); if(tableParts is null) sheet.Root.Add(draw); else tableParts.AddBeforeSelf(draw);
        }
        if(rels.HasElements) BoundedXml.Put(package,"xl/worksheets/_rels/sheet1.xml.rels",new XDocument(rels));
    }
    private static XElement Marker(string tag,int col,int row)=>new(D+tag,new XElement(D+"col",col),new XElement(D+"colOff",0),new XElement(D+"row",row),new XElement(D+"rowOff",0));
    private static XElement Val(XNamespace ns,string name,object value)=>new(ns+name,new XAttribute("val",value));
    private static XDocument ChartXml(ChartDefinition chart,IReadOnlyList<ChartPoint> points,string sheetName)
    {
        string prefix="'"+sheetName.Replace("'","''",StringComparison.Ordinal)+"'!";
        string AbsoluteRange(KHZ.Sheet.Core.CellRange range) =>
            $"${WorksheetSession.ColumnName(range.TopLeft.Column)}${range.TopLeft.Row+1}:${WorksheetSession.ColumnName(range.BottomRight.Column)}${range.BottomRight.Row+1}";
        var categoryCache=new XElement(C+"strCache",Val(C,"ptCount",points.Count));
        var valueCache=new XElement(C+"numCache",new XElement(C+"formatCode","General"),Val(C,"ptCount",points.Count));
        for(int i=0;i<points.Count;++i) {
            categoryCache.Add(new XElement(C+"pt",new XAttribute("idx",i),new XElement(C+"v",points[i].Category)));
            if(points[i].Value is KhzRational q) valueCache.Add(new XElement(C+"pt",new XAttribute("idx",i),
                new XElement(C+"v",((double)q.Numerator/q.Denominator).ToString("R",CultureInfo.InvariantCulture))));
        }
        var series=new XElement(C+"ser",Val(C,"idx",0),Val(C,"order",0),
            new XElement(C+"tx",new XElement(C+"v",chart.Title)),
            new XElement(C+"cat",new XElement(C+"strRef",new XElement(C+"f",prefix+AbsoluteRange(chart.Categories)),categoryCache)),
            new XElement(C+"val",new XElement(C+"numRef",new XElement(C+"f",prefix+AbsoluteRange(chart.Values)),valueCache)));
        bool line=chart.Kind==ChartKind.Line;
        var plot=new XElement(C+(line?"lineChart":"barChart"));
        if(!line) plot.Add(Val(C,"barDir",chart.Kind==ChartKind.Bar?"bar":"col"));
        plot.Add(Val(C,"grouping",line?"standard":"clustered"),series,Val(C,"axId",1),Val(C,"axId",2));
        XElement Axis(string name,int id,int cross,string position)=>new(C+name,Val(C,"axId",id),
            new XElement(C+"scaling",Val(C,"orientation","minMax")),Val(C,"delete",0),Val(C,"axPos",position),
            Val(C,"tickLblPos","nextTo"),Val(C,"crossAx",cross),Val(C,"crosses","autoZero"));
        var cat=Axis("catAx",1,2,chart.Kind==ChartKind.Bar?"l":"b"); cat.Add(Val(C,"auto",1),Val(C,"lblAlgn","ctr"),Val(C,"lblOffset",100));
        var val=Axis("valAx",2,1,chart.Kind==ChartKind.Bar?"b":"l"); val.Add(Val(C,"crossBetween","between"));
        var title=new XElement(C+"title",new XElement(C+"tx",new XElement(C+"rich",new XElement(A+"bodyPr"),new XElement(A+"lstStyle"),
            new XElement(A+"p",new XElement(A+"r",new XElement(A+"t",chart.Title))))),Val(C,"overlay",0));
        return new XDocument(new XElement(C+"chartSpace",new XElement(C+"chart",title,
            new XElement(C+"plotArea",new XElement(C+"layout"),plot,cat,val),Val(C,"plotVisOnly",1),Val(C,"dispBlanksAs","gap"))));
    }
    private static void ApplyTheme(OpcPackage package,XDocument types,XDocument rels,SheetTheme theme)
    {
        XElement colors=new(A+"clrScheme",new XAttribute("name",theme.Name));
        string[] names=["dk1","lt1","dk2","lt2","accent1","accent2","accent3","accent4","accent5","accent6","hlink","folHlink"];
        string[] palette=[theme.Text,theme.Background,theme.Panel,theme.Band,theme.Accent,"#D9730D","#318755","#7857A4","#B63C50","#339BA3","#0563C1","#954F72"];
        for(int i=0;i<names.Length;++i) colors.Add(new XElement(A+names[i],new XElement(A+"srgbClr",new XAttribute("val",Argb(palette[i])[2..]))));
        XElement Font(string tag)=>new(A+tag,new XElement(A+"latin",new XAttribute("typeface",CellFormatDefaults.FontFamily)),new XElement(A+"ea",new XAttribute("typeface","")),new XElement(A+"cs",new XAttribute("typeface","")));
        XElement Fill()=>new(A+"solidFill",new XElement(A+"schemeClr",new XAttribute("val","phClr")));
        var format=new XElement(A+"fmtScheme",new XAttribute("name",theme.Name),
            new XElement(A+"fillStyleLst",Enumerable.Range(0,3).Select(_=>Fill())),
            new XElement(A+"lnStyleLst",Enumerable.Range(0,3).Select(i=>new XElement(A+"ln",new XAttribute("w",(i+1)*12700),Fill(),new XElement(A+"prstDash",new XAttribute("val","solid"))))),
            new XElement(A+"effectStyleLst",Enumerable.Range(0,3).Select(_=>new XElement(A+"effectStyle",new XElement(A+"effectLst")))),
            new XElement(A+"bgFillStyleLst",Enumerable.Range(0,3).Select(_=>Fill())));
        BoundedXml.Put(package,"xl/theme/theme1.xml",new XDocument(new XElement(A+"theme",new XAttribute("name",theme.Name),
            new XElement(A+"themeElements",colors,new XElement(A+"fontScheme",new XAttribute("name",theme.Name),Font("majorFont"),Font("minorFont")),format))));
        Override(types,"xl/theme/theme1.xml","application/vnd.openxmlformats-officedocument.theme+xml");
        rels.Root!.Add(Rel("khzTheme","theme","theme/theme1.xml"));
    }
}
