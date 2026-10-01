using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using KHZ.Sheet.Core;
using CoreRange = KHZ.Sheet.Core.CellRange;

namespace KHZ.Sheet.Desktop;

internal static partial class XlsxPresentationSerializer
{
    private static void Children(XElement parent, XNamespace ns, params string[] names)
    {
        foreach(var child in parent.Elements())
            if(child.Name.Namespace!=ns || !names.Contains(child.Name.LocalName,StringComparer.Ordinal))
                throw new NotSupportedException("Unsupported element: "+child.Name);
    }
    private static void Attributes(XElement element, params string[] names)
    {
        foreach(var attribute in element.Attributes().Where(a=>!a.IsNamespaceDeclaration))
            if(attribute.Name.Namespace!=XNamespace.None || !names.Contains(attribute.Name.LocalName,StringComparer.Ordinal))
                throw new NotSupportedException("Unsupported attribute: "+attribute.Name);
    }
    private static int Int(XElement element,string name,int fallback=0)
    {
        string? raw=(string?)element.Attribute(name);
        if(raw is null) return fallback;
        if(!int.TryParse(raw,NumberStyles.None,CultureInfo.InvariantCulture,out int result)) throw new InvalidDataException("Invalid "+name);
        return result;
    }
    private static double Real(XElement element,string name,double fallback)
    {
        string? raw=(string?)element.Attribute(name);
        if(raw is null) return fallback;
        if(!double.TryParse(raw,NumberStyles.Float,CultureInfo.InvariantCulture,out double n) || !double.IsFinite(n)) throw new InvalidDataException("Invalid "+name);
        return n;
    }
    private static bool Flag(XElement? e,string name="val",bool fallback=false)
    {
        if(e is null) return false;
        string? v=(string?)e.Attribute(name);
        return v is null ? fallback : v is "1" or "true" ? true : v is "0" or "false" ? false : throw new InvalidDataException("Invalid boolean");
    }
    private static XElement[] Entries(XElement? parent,string child,int max)
    {
        if(parent is null) return [];
        var items=parent.Elements(S+child).ToArray();
        if(items.Length>max || Int(parent,"count",items.Length)!=items.Length) throw new InvalidDataException("Style table count");
        return items;
    }
    private static CellFormat[] ReadStyles(OpcPackage package)
    {
        if(package.TryGetPartLength(StylesPart,out _)!=SheetStatus.Ok) return [CellFormat.Empty];
        XElement root=BoundedXml.Load(package,StylesPart).Root ?? throw new InvalidDataException("Missing styles");
        if(root.Name!=S+"styleSheet") throw new InvalidDataException("Invalid styles namespace");
        Children(root,S,"numFmts","fonts","fills","borders","cellStyleXfs","cellXfs","cellStyles","dxfs","tableStyles");
        if(root.Elements().GroupBy(e=>e.Name).Any(g=>g.Count()>1)) throw new InvalidDataException("Duplicate style table");
        if(root.Element(S+"dxfs")?.HasElements==true) throw new NotSupportedException("Differential formats");
        var fonts=Entries(root.Element(S+"fonts"),"font",4096);
        var fills=Entries(root.Element(S+"fills"),"fill",4096);
        var borders=Entries(root.Element(S+"borders"),"border",4096);
        var xfs=Entries(root.Element(S+"cellXfs"),"xf",4096);
        if(fonts.Length==0 || fills.Length<2 || borders.Length==0 || xfs.Length==0) throw new InvalidDataException("Missing style tables");
        var custom=Entries(root.Element(S+"numFmts"),"numFmt",512).ToDictionary(e=>Int(e,"numFmtId"),e=>(string?)e.Attribute("formatCode") ?? "");
        string? Color(XElement? color)
        {
            if(color is null) return null;
            string? rgb=(string?)color.Attribute("rgb");
            if(rgb is null) throw new NotSupportedException("Only explicit RGB style colors are supported");
            return "#"+Argb(rgb);
        }
        CellBorder? Edge(XElement border,string side)
        {
            XElement? e=border.Element(S+side); string? style=(string?)e?.Attribute("style");
            return style is null ? null : new CellBorder(style,Color(e!.Element(S+"color")) ?? "#FF000000");
        }
        var result=new List<CellFormat>();
        foreach(var xf in xfs)
        {
            Children(xf,S,"alignment");
            Attributes(xf,"fontId","fillId","borderId","numFmtId","xfId","applyFont","applyFill","applyBorder","applyAlignment","applyNumberFormat");
            int fi=Int(xf,"fontId"), fillId=Int(xf,"fillId"), bi=Int(xf,"borderId"), ni=Int(xf,"numFmtId");
            if(fi>=fonts.Length || fillId>=fills.Length || bi>=borders.Length || Int(xf,"xfId")!=0) throw new InvalidDataException("Style index");
            CellNumberFormat number;
            if(ni<164 && Enum.IsDefined(typeof(CellNumberFormat),ni)) number=(CellNumberFormat)ni;
            else if(ni==14) number=CellNumberFormat.IsoDate;
            else if(custom.TryGetValue(ni,out string? code)) number=code switch {
                "# ?/??????????????????"=>CellNumberFormat.Fraction, "\"USD \"#,##0.00"=>CellNumberFormat.Currency,
                "yyyy-mm-dd"=>CellNumberFormat.IsoDate, _=>throw new NotSupportedException("Number format: "+code) };
            else throw new NotSupportedException("Number format id: "+ni);
            XElement font=fonts[fi],border=borders[bi]; XElement? pattern=fills[fillId].Element(S+"patternFill");
            Children(font,S,"name","sz","b","i","u","color");
            Children(fills[fillId],S,"patternFill");
            Children(border,S,"left","right","top","bottom","diagonal");
            if(border.Element(S+"diagonal") is XElement diagonal && (diagonal.HasAttributes || diagonal.HasElements)) throw new NotSupportedException("Diagonal border");
            if(font.Element(S+"u")?.Attribute("val") is XAttribute underline && underline.Value!="single") throw new NotSupportedException("Underline style");
            string? fill=(string?)pattern?.Attribute("patternType");
            if(fill is not (null or "none" or "gray125" or "solid")) throw new NotSupportedException("Pattern fill");
            XElement? align=xf.Element(S+"alignment");
            if(align is not null) Attributes(align,"wrapText","horizontal","vertical");
            CellTextAlignment? horizontal=((string?)align?.Attribute("horizontal")) switch {
                null or "general"=>null, "left"=>CellTextAlignment.Left, "center"=>CellTextAlignment.Center,
                "right"=>CellTextAlignment.Right,_=>throw new NotSupportedException("Horizontal alignment") };
            CellVerticalAlignment? vertical=((string?)align?.Attribute("vertical")) switch {
                null=>null,"top"=>CellVerticalAlignment.Top,"center"=>CellVerticalAlignment.Center,
                "bottom"=>CellVerticalAlignment.Bottom,_=>throw new NotSupportedException("Vertical alignment") };
            var f=new CellFormat(FontFamily:(string?)font.Element(S+"name")?.Attribute("val"),
                FontSize:font.Element(S+"sz") is XElement sz ? Real(sz,"val",11):null,
                Bold:Flag(font.Element(S+"b"),fallback:true),Italic:Flag(font.Element(S+"i"),fallback:true),
                Underline:font.Element(S+"u") is not null,Foreground:Color(font.Element(S+"color")),
                Background:fill=="solid"?Color(pattern?.Element(S+"fgColor")):null,
                Alignment:horizontal,VerticalAlignment:vertical,WrapText:Flag(align,"wrapText"),NumberFormat:number,
                LeftBorder:Edge(border,"left"),RightBorder:Edge(border,"right"),TopBorder:Edge(border,"top"),BottomBorder:Edge(border,"bottom"));
            if(!f.IsValid) throw new InvalidDataException("Invalid style value");
            result.Add(f);
        }
        return result.ToArray();
    }
    private static string Target(string source,string raw)
    {
        if(string.IsNullOrEmpty(raw) || raw.Contains('\\') || raw.Contains(':') || raw.Contains('%') || raw.Contains('#')) throw new InvalidDataException("Invalid relationship target");
        var uri=new Uri(new Uri("https://package.invalid/"+source),raw);
        if(uri.Host!="package.invalid") throw new InvalidDataException("External relationship");
        return uri.AbsolutePath.TrimStart('/');
    }
    private static Dictionary<string,(string Type,string Path)> Relationships(OpcPackage p,string source)
    {
        string dir=source.Contains('/') ? source[..(source.LastIndexOf('/')+1)] : "";
        string file=source[(source.LastIndexOf('/')+1)..];
        string relpath=dir+"_rels/"+file+".rels";
        if(p.TryGetPartLength(relpath,out _)!=SheetStatus.Ok) return new();
        XElement root=BoundedXml.Load(p,relpath).Root ?? throw new InvalidDataException("Relationship root");
        if(root.Name!=P+"Relationships") throw new InvalidDataException("Relationship namespace");
        Children(root,P,"Relationship");
        var result=new Dictionary<string,(string,string)>(StringComparer.Ordinal);
        foreach(var e in root.Elements(P+"Relationship")) {
            string id=(string?)e.Attribute("Id") ?? "";
            string type=(string?)e.Attribute("Type") ?? "";
            if(id.Length==0 || e.Attribute("TargetMode") is not null || !type.StartsWith(R.NamespaceName+"/",StringComparison.Ordinal)) throw new NotSupportedException("Unsupported or external relationship");
            type=type[(R.NamespaceName.Length+1)..];
            if(type is not ("officeDocument" or "worksheet" or "styles" or "sharedStrings" or "theme" or "drawing" or "chart" or "table")) throw new NotSupportedException("Relationship: "+type);
            string path=Target(source,(string?)e.Attribute("Target") ?? "");
            if(p.TryGetPartLength(path,out _)!=SheetStatus.Ok || !result.TryAdd(id,(type,path))) throw new InvalidDataException("Missing or duplicate relationship");
        }
        return result;
    }
    private static void ValidateParts(OpcPackage p)
    {
        if(p.DroppedEntries.Count!=0 || !p.HasContentTypes) throw new InvalidDataException("Duplicate or missing OPC parts");
        string[] known=["[Content_Types].xml","_rels/.rels","xl/workbook.xml",WorkbookRelsPart,SheetPart,StylesPart,
            "xl/sharedStrings.xml","xl/theme/theme1.xml","xl/worksheets/_rels/sheet1.xml.rels",
            "xl/drawings/drawing1.xml","xl/drawings/_rels/drawing1.xml.rels"];
        foreach(string part in p.PartNames) {
            if(!known.Contains(part,StringComparer.Ordinal) && !Regex.IsMatch(part,@"^xl/(tables/table|charts/chart)[1-9][0-9]{0,2}\.xml$",RegexOptions.CultureInvariant))
                throw new NotSupportedException("Unsupported package part: "+part);
            if(part.EndsWith(".xml",StringComparison.Ordinal) || part.EndsWith(".rels",StringComparison.Ordinal)) _=BoundedXml.Load(p,part);
        }
        var root=Relationships(p,"");
        if(root.Count!=1 || root.Values.Single()!= ("officeDocument","xl/workbook.xml")) throw new NotSupportedException("Workbook relationship");
        var rels=Relationships(p,"xl/workbook.xml");
        if(rels.Values.Any(t=>t.Type is not ("worksheet" or "styles" or "sharedStrings" or "theme"))) throw new NotSupportedException("Workbook relationship type");
        foreach(var (type,path) in new[] {("styles",StylesPart),("sharedStrings","xl/sharedStrings.xml"),("theme","xl/theme/theme1.xml")})
        {
            var matches=rels.Values.Where(t=>t.Type==type).ToArray();
            bool present=p.TryGetPartLength(path,out _)==SheetStatus.Ok;
            if(matches.Length!=(present?1:0) || (present && matches[0].Path!=path)) throw new InvalidDataException("Noncanonical "+type+" relationship");
        }
        XNamespace contentNamespace="http://schemas.openxmlformats.org/package/2006/content-types";
        XElement types=BoundedXml.Load(p,ContentTypesPart).Root!;
        if(types.Name!=contentNamespace+"Types") throw new InvalidDataException("Content-types namespace");
        Children(types,contentNamespace,"Default","Override");
        var overrides=types.Elements(contentNamespace+"Override").ToDictionary(e=>(string?)e.Attribute("PartName") ?? "",e=>(string?)e.Attribute("ContentType") ?? "");
        foreach(string part in p.PartNames.Where(n=>n.StartsWith("xl/",StringComparison.Ordinal) && n.EndsWith(".xml",StringComparison.Ordinal)))
        {
            string suffix=part switch {"xl/workbook.xml"=>"spreadsheetml.sheet.main",StylesPart=>"spreadsheetml.styles",SheetPart=>"spreadsheetml.worksheet",
                "xl/sharedStrings.xml"=>"spreadsheetml.sharedStrings","xl/theme/theme1.xml"=>"theme",
                _ when part.StartsWith("xl/tables/",StringComparison.Ordinal)=>"spreadsheetml.table",
                _ when part.StartsWith("xl/charts/",StringComparison.Ordinal)=>"drawingml.chart",_=>"drawing"};
            if(!overrides.TryGetValue("/"+part,out string? contentType) || contentType!="application/vnd.openxmlformats-officedocument."+suffix+"+xml")
                throw new NotSupportedException("Content type: "+part);
        }
        var book=BoundedXml.Load(p,"xl/workbook.xml");
        if(book.Root?.Name!=S+"workbook") throw new InvalidDataException("Workbook namespace");
        Children(book.Root,S,"sheets","calcPr");
        var sheets=book.Root?.Element(S+"sheets")?.Elements(S+"sheet").ToArray();
        if(sheets?.Length!=1) throw new NotSupportedException("Desktop imports one worksheet per XLSX");
        string id=(string?)sheets[0].Attribute(R+"id") ?? "";
        if(!rels.TryGetValue(id,out var target) || target!=("worksheet",SheetPart)) throw new NotSupportedException("Worksheet relationship");
    }
    public static bool TryLoad(string path,WorksheetSession session,out string message)
    {
        message="xlsx import failed";
        try {
            using FileStream stream=File.OpenRead(path);
            SheetStatus status=OpcPackage.TryOpen(stream,out OpcPackage? package);
            if(status!=SheetStatus.Ok || package is null) {message=SheetStatusText.Name(status);return false;}
            using(package) {
                ValidateParts(package);
                var styles=ReadStyles(package);
                XDocument sheet=BoundedXml.Load(package,SheetPart);
                if(sheet.Root?.Name!=S+"worksheet") throw new InvalidDataException("Worksheet root");
                Children(sheet.Root,S,"dimension","sheetViews","cols","sheetData","drawing","tableParts");
                if(sheet.Root.Elements().GroupBy(e=>e.Name).Any(g=>g.Count()>1) || sheet.Root.Element(S+"sheetData") is null)
                    throw new InvalidDataException("Missing or duplicate worksheet section");
                if(sheet.Root.Element(S+"dimension")?.Attribute("ref") is XAttribute dimension) CheckRange(dimension.Value,session);
                string[] shared=[];
                if(package.TryGetPartLength("xl/sharedStrings.xml",out _) == SheetStatus.Ok)
                    shared=BoundedXml.Load(package,"xl/sharedStrings.xml").Root!.Elements(S+"si").Select(e=>DecodeText(string.Concat(e.Descendants(S+"t").Select(t=>t.Value)))).ToArray();
                var seenRows=new HashSet<int>();
                Children(sheet.Root.Element(S+"sheetData")!,S,"row");
                foreach(var row in sheet.Root.Element(S+"sheetData")!.Elements(S+"row")) {
                    Attributes(row,"r","ht","customHeight"); Children(row,S,"c");
                    int rowIndex=Int(row,"r")-1;
                    if(rowIndex<0 || rowIndex>=session.Grid.Rows.Count || !seenRows.Add(rowIndex)) throw new InvalidDataException("Row limit or duplicate row");
                    if(row.Attribute("ht") is not null && !session.SetRowHeight(rowIndex,Real(row,"ht",0))) throw new InvalidDataException("Row height");
                    foreach(var cell in row.Elements(S+"c")) {
                        Attributes(cell,"r","s","t"); Children(cell,S,"f","v","is");
                        if(cell.Elements().GroupBy(e=>e.Name).Any(g=>g.Count()>1)) throw new InvalidDataException("Duplicate cell value");
                        if(CellAddress.TryParse((string?)cell.Attribute("r") ?? "",out var address)!=SheetStatus.Ok || address.Row!=rowIndex || address.Column>=session.Grid.Columns.Count) throw new InvalidDataException("Cell reference");
                        int si=Int(cell,"s"); if(si>=styles.Length) throw new InvalidDataException("Cell style index");
                        XElement? f=cell.Element(S+"f"); string type=(string?)cell.Attribute("t") ?? "n";
                        string? input=cell.Element(S+"v")?.Value; bool text=false;
                        if(f is not null) { if(f.HasAttributes) throw new NotSupportedException("Shared/array formulas"); input="="+f.Value; }
                        else switch(type) {
                            case "n": if(input is not null && !decimal.TryParse(input,NumberStyles.Float,CultureInfo.InvariantCulture,out _)) throw new InvalidDataException("Numeric cell"); break;
                            case "b": input=input switch {"0"=>"FALSE","1"=>"TRUE",_=>throw new InvalidDataException("Boolean cell")};break;
                            case "e": break;
                            case "s": if(!int.TryParse(input,NumberStyles.None,CultureInfo.InvariantCulture,out int index) || index<0 || index>=shared.Length) throw new InvalidDataException("Shared string index"); input=shared[index];text=true;break;
                            case "inlineStr": input=DecodeText(string.Concat(cell.Element(S+"is")?.Descendants(S+"t").Select(e=>e.Value) ?? []));text=true;break;
                            default: throw new NotSupportedException("Cell type: "+type);
                        }
                        if(!session.ImportValue(address.Row,address.Column,input ?? "",text,out message)) return false;
                        if(!styles[si].IsEmpty) session.ApplyFormat([(address.Row,address.Column)],styles[si],out _);
                    }
                }
                foreach(var col in sheet.Root.Element(S+"cols")?.Elements(S+"col") ?? []) {
                    Attributes(col,"min","max","width","customWidth");
                    int min=Int(col,"min"),max=Int(col,"max"); double width=Real(col,"width",0);
                    if(min<1 || max<min || max>session.Grid.Columns.Count) throw new InvalidDataException("Column limit");
                    for(int c=min-1;c<max;++c) if(session.ColumnWidths.ContainsKey(c) || !session.SetColumnWidth(c,width)) throw new InvalidDataException("Column width");
                }
                XElement? pane=sheet.Root.Element(S+"sheetViews")?.Element(S+"sheetView")?.Element(S+"pane");
                if(pane is not null) {
                    Attributes(pane,"xSplit","ySplit","topLeftCell","activePane","state");
                    int columns=Int(pane,"xSplit"), rows=Int(pane,"ySplit");
                    if((string?)pane.Attribute("state")!="frozen" || columns<0 || rows<0 ||
                       columns>=session.Grid.Columns.Count || rows>=session.Grid.Rows.Count || columns+rows==0)
                        throw new NotSupportedException("Invalid or unsupported frozen pane");
                    string expected=WorksheetSession.ColumnName(columns)+(rows+1).ToString(CultureInfo.InvariantCulture);
                    string activePane=columns>0?(rows>0?"bottomRight":"topRight"):"bottomLeft";
                    if(!string.Equals((string?)pane.Attribute("topLeftCell"),expected,StringComparison.OrdinalIgnoreCase) ||
                       !string.Equals((string?)pane.Attribute("activePane"),activePane,StringComparison.Ordinal))
                        throw new InvalidDataException("Frozen pane coordinates do not match its splits");
                    session.SetFrozenColumns(columns);
                    session.SetFrozenRows(rows);
                }
                if(package.TryGetPartLength("xl/theme/theme1.xml",out _) == SheetStatus.Ok) {
                    string? name=(string?)BoundedXml.Load(package,"xl/theme/theme1.xml").Root?.Attribute("name");
                    SheetTheme? theme=SheetTheme.Presets.SingleOrDefault(t=>t.Name==name);
                    if(theme is not null) session.SetTheme(theme);
                }
                if(!session.Recalculate(out message)) return false;
                ReadObjects(package,sheet,session);
                message=$"xlsx · {session.NativeCellCount} cells · styles/layout/tables/charts imported"; return true;
            }
        } catch(Exception ex) when(ex is InvalidDataException or IOException or UnauthorizedAccessException or XmlException or FormatException or ArgumentException or NotSupportedException or OverflowException or InvalidOperationException) {
            message=(ex is NotSupportedException ? "ERR_UNSUPPORTED" : "ERR_FORMAT")+" · "+ex.Message;return false;
        }
    }
    private static string DecodeText(string text)
    {
        string decoded=Regex.Replace(text,@"_x([0-9a-fA-F]{4})_",m=>((char)int.Parse(m.Groups[1].Value,NumberStyles.HexNumber,CultureInfo.InvariantCulture)).ToString());
        for(int i=0;i<decoded.Length;++i)
        {
            if(char.IsHighSurrogate(decoded[i]))
            {
                if(i+1>=decoded.Length || !char.IsLowSurrogate(decoded[++i])) throw new InvalidDataException("Unpaired SpreadsheetML surrogate");
            }
            else if(char.IsLowSurrogate(decoded[i])) throw new InvalidDataException("Unpaired SpreadsheetML surrogate");
        }
        return decoded;
    }
    private static CoreRange CheckRange(string text,WorksheetSession session)
    {
        if(CoreRange.TryParse(text,out var r)!=SheetStatus.Ok || r.BottomRight.Row>=session.Grid.Rows.Count || r.BottomRight.Column>=session.Grid.Columns.Count)
            throw new InvalidDataException("Range exceeds desktop viewport");
        return r;
    }
    private static void ReadObjects(OpcPackage p,XDocument sheet,WorksheetSession session)
    {
        var rels=Relationships(p,SheetPart); List<TableDefinition> tables=new(); List<ChartDefinition> charts=new();
        foreach(var item in Entries(sheet.Root!.Element(S+"tableParts"),"tablePart",128)) {
            string id=(string?)item.Attribute(R+"id") ?? "";
            if(!rels.TryGetValue(id,out var target) || target.Type!="table") throw new InvalidDataException("Table relationship");
            XElement t=BoundedXml.Load(p,target.Path).Root!;
            if(t.Name!=S+"table") throw new InvalidDataException("Table root");
            Children(t,S,"autoFilter","tableColumns","tableStyleInfo");
            if(t.Element(S+"autoFilter")?.HasElements==true) throw new NotSupportedException("Table filter state");
            var range=CheckRange((string?)t.Attribute("ref") ?? "",session);
            var columnNodes=Entries(t.Element(S+"tableColumns"),"tableColumn",session.Grid.Columns.Count);
            foreach(var column in columnNodes)
                if(column.HasElements || ((string?)column.Attribute("totalsRowFunction") is string function && function!="sum")) throw new NotSupportedException("Table column formula");
            string[] columns=columnNodes.Select(c=>(string?)c.Attribute("name") ?? "").ToArray();
            string name=(string?)t.Attribute("name") ?? "";
            bool totals=Int(t,"totalsRowCount")==1;
            if(Int(t,"totalsRowCount")>1) throw new InvalidDataException("Totals row count");
            if(!session.CreateTable(new CellRange(range.TopLeft.Row,range.TopLeft.Column,range.BottomRight.Row,range.BottomRight.Column),name,totals,out string error)) throw new InvalidDataException(error);
            if(!session.Tables.Last().Columns.SequenceEqual(columns)) throw new InvalidDataException("Table headers disagree with cells");
        }
        foreach(var draw in sheet.Root.Elements(S+"drawing")) {
            if(!rels.TryGetValue((string?)draw.Attribute(R+"id") ?? "",out var target) || target.Type!="drawing") throw new InvalidDataException("Drawing relationship");
            var drawing=BoundedXml.Load(p,target.Path); var drawingRels=Relationships(p,target.Path);
            foreach(var node in drawing.Descendants(C+"chart")) {
                if(!drawingRels.TryGetValue((string?)node.Attribute(R+"id") ?? "",out var ct) || ct.Type!="chart") throw new InvalidDataException("Chart relationship");
                var chart=BoundedXml.Load(p,ct.Path);
                var plot=chart.Descendants(C+"plotArea").Single();
                var kindNode=plot.Elements().SingleOrDefault(e=>e.Name==C+"barChart" || e.Name==C+"lineChart") ?? throw new NotSupportedException("Chart type");
                if(kindNode.Name==C+"barChart" && (string?)kindNode.Element(C+"barDir")?.Attribute("val") is not ("bar" or "col")) throw new InvalidDataException("Bar direction");
                var series=kindNode.Elements(C+"ser").Single();
                CoreRange Range(string tag)
                {
                    string f=series.Element(C+tag)?.Descendants(C+"f").Single().Value ?? "";
                    int split=f.LastIndexOf('!');
                    string sheetName=(string?)BoundedXml.Load(p,"xl/workbook.xml").Root?.Element(S+"sheets")?.Element(S+"sheet")?.Attribute("name") ?? "";
                    if(split<0 || (f[..split]!=sheetName && f[..split]!="'"+sheetName.Replace("'","''")+"'"))
                        throw new NotSupportedException("Chart must refer to the imported worksheet");
                    return CheckRange(f[(split+1)..],session);
                }
                var kind=kindNode.Name==C+"lineChart"?ChartKind.Line:((string?)kindNode.Element(C+"barDir")?.Attribute("val")=="bar"?ChartKind.Bar:ChartKind.Column);
                var definition=new ChartDefinition(Guid.NewGuid(),string.Concat(chart.Descendants(C+"title").Descendants(A+"t").Select(e=>e.Value)),kind,Range("cat"),Range("val"));
                if(!definition.IsValid || charts.Count>=32) throw new InvalidDataException("Chart range"); charts.Add(definition);
            }
        }
        session.ImportObjects(tables,charts);
    }
}
