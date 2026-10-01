using System.Data;
using System.Globalization;
using System.Text;
using KHZ.Sheet.Core;
using CoreRange=KHZ.Sheet.Core.CellRange;

namespace KHZ.Sheet.Desktop;

public sealed partial class WorksheetSession
{
    private enum StructureAxis { Row, Column }

    public bool InsertRow(int row,out string message) => TransformStructure(StructureAxis.Row,row,insert:true,out message);
    public bool DeleteRow(int row,out string message) => TransformStructure(StructureAxis.Row,row,insert:false,out message);
    public bool InsertColumn(int column,out string message) => TransformStructure(StructureAxis.Column,column,insert:true,out message);
    public bool DeleteColumn(int column,out string message) => TransformStructure(StructureAxis.Column,column,insert:false,out message);

    private bool TransformStructure(StructureAxis axis,int index,bool insert,out string message)
    {
        int limit=axis==StructureAxis.Row?Grid.Rows.Count:Grid.Columns.Count;
        if(!EngineAvailable) { message=EngineLoadError??EngineSummary; return false; }
        if(index<0 || index>=limit) { message="row or column is outside this worksheet"; return false; }
        if(insert && _inputs.Keys.Any(key=>
        {
            DecodeKey(key,out int row,out int column);
            return axis==StructureAxis.Row?row==limit-1:column==limit-1;
        }))
        { message="insertion would discard populated cells at the worksheet edge"; return false; }

        if(axis==StructureAxis.Column && _tables.Any(table=>insert
            ? index>table.Range.TopLeft.Column && index<=table.Range.BottomRight.Column
            : table.Range.TopLeft.Column<=index && index<=table.Range.BottomRight.Column))
        { message="insert or delete table columns through the table editor"; return false; }

        Dictionary<long,string> inputs=new();
        foreach(var pair in _inputs)
        {
            DecodeKey(pair.Key,out int row,out int column);
            if(!TryMapCoordinate(axis,index,insert,ref row,ref column,out bool removed)) continue;
            if(removed) continue;
            string value=pair.Value;
            if(value.StartsWith('=') && !TryAdjustFormulaReferences(value,axis,index,insert,out value))
            { message="a formula reference could not be adjusted safely"; return false; }
            inputs[Key(row,column)]=value;
        }

        List<(long Key,CellFormat Format)> formats=new();
        foreach(var pair in _formats)
        {
            DecodeKey(pair.Key,out int row,out int column);
            if(!TryMapCoordinate(axis,index,insert,ref row,ref column,out bool removed) || removed) continue;
            if(row>=Grid.Rows.Count || column>=Grid.Columns.Count) continue;
            formats.Add((Key(row,column),pair.Value));
        }

        List<TableDefinition> tables=new();
        foreach(TableDefinition table in _tables)
        {
            if(!TryMapRange(table.Range,axis,index,insert,out CoreRange range))
            { message="the change would leave a table with an invalid range"; return false; }
            if(range.BottomRight.Row>=Grid.Rows.Count || range.BottomRight.Column>=Grid.Columns.Count)
            { message="the change would move a table beyond the worksheet edge"; return false; }
            int minimum=table.Totals?3:2;
            if(range.RowCount<minimum) { message="the change would make a table too small"; return false; }
            tables.Add(table with { Range=range });
        }
        List<ChartDefinition> charts=new();
        foreach(ChartDefinition chart in _charts)
        {
            if(!TryMapRange(chart.Categories,axis,index,insert,out CoreRange categories) ||
                !TryMapRange(chart.Values,axis,index,insert,out CoreRange values))
            { message="the change would leave a chart with an invalid range"; return false; }
            if(categories.BottomRight.Row>=Grid.Rows.Count || categories.BottomRight.Column>=Grid.Columns.Count ||
                values.BottomRight.Row>=Grid.Rows.Count || values.BottomRight.Column>=Grid.Columns.Count)
            { message="the change would move a chart beyond the worksheet edge"; return false; }
            ChartDefinition adjusted=chart with { Categories=categories,Values=values };
            if(!adjusted.IsValid) { message="the change would invalidate a chart"; return false; }
            charts.Add(adjusted);
        }
        List<TableFormat> tableFormats=new();
        foreach(TableFormat format in _tableFormats)
        {
            if(!TryMapRange(format.Range,axis,index,insert,out CoreRange range)) continue;
            if(range.BottomRight.Row>=Grid.Rows.Count || range.BottomRight.Column>=Grid.Columns.Count) continue;
            tableFormats.Add(format with { Range=new CellRange(range.TopLeft.Row,range.TopLeft.Column,range.BottomRight.Row,range.BottomRight.Column) });
        }

        WorksheetSession candidate=new(Name);
        foreach(var pair in inputs.OrderBy(item=>item.Value.StartsWith('=')).ThenBy(item=>item.Key))
        {
            DecodeKey(pair.Key,out int row,out int column);
            bool text=pair.Value.StartsWith('\'');
            string raw=text?pair.Value[1..]:pair.Value;
            if(candidate.ImportValue(row,column,raw,text,out message)) continue;
            candidate.Dispose();
            return false;
        }
        if(!candidate.Recalculate(out message)) { candidate.Dispose(); return false; }

        foreach(DataRow row in Grid.Rows)
            foreach(DataColumn column in Grid.Columns)
                row[column]=string.Empty;
        AdoptValues(candidate);
        using(candidate)
        {
            _formats.Clear();
            foreach(var item in formats) _formats[item.Key]=item.Format;
            _tableFormats.Clear(); _tableFormats.AddRange(tableFormats);
            _tables.Clear();
            foreach(TableDefinition table in tables)
            {
                string[] headers=Enumerable.Range(table.Range.TopLeft.Column,table.Range.ColumnCount)
                    .Select(column=>Grid.Rows[table.Range.TopLeft.Row][column]?.ToString()??string.Empty).ToArray();
                _tables.Add(table with { Columns=Array.AsReadOnly(headers) });
            }
            _charts.Clear(); _charts.AddRange(charts);
        }

        if(axis==StructureAxis.Row) ShiftDimensions(_rowHeights,index,insert,limit);
        else ShiftDimensions(_columnWidths,index,insert,limit);
        _undo.Clear(); _redo.Clear(); MarkDirty();
        message=$"{(insert?"inserted":"deleted")} {(axis==StructureAxis.Row?"row":"column")} · history cleared";
        return true;
    }

    private static bool TryMapCoordinate(StructureAxis axis,int index,bool insert,ref int row,ref int column,out bool removed)
    {
        removed=false;
        int coordinate=axis==StructureAxis.Row?row:column;
        if(!insert && coordinate==index) { removed=true; return true; }
        if((insert && coordinate>=index)||(!insert && coordinate>index))
            coordinate+=insert?1:-1;
        if(axis==StructureAxis.Row) row=coordinate; else column=coordinate;
        return true;
    }

    private static bool TryMapRange(CoreRange source,StructureAxis axis,int index,bool insert,out CoreRange mapped)
    {
        int start=axis==StructureAxis.Row?source.TopLeft.Row:source.TopLeft.Column;
        int end=axis==StructureAxis.Row?source.BottomRight.Row:source.BottomRight.Column;
        if(insert)
        {
            if(index<=start) { ++start; ++end; }
            else if(index<=end) ++end;
        }
        else if(index<start) { --start; --end; }
        else if(index<=end) --end;
        if(start>end) { mapped=default; return false; }
        int top=axis==StructureAxis.Row?start:source.TopLeft.Row;
        int bottom=axis==StructureAxis.Row?end:source.BottomRight.Row;
        int left=axis==StructureAxis.Column?start:source.TopLeft.Column;
        int right=axis==StructureAxis.Column?end:source.BottomRight.Column;
        if(CellAddress.TryCreate(left,top,out CellAddress first)!=SheetStatus.Ok ||
            CellAddress.TryCreate(right,bottom,out CellAddress last)!=SheetStatus.Ok)
        { mapped=default; return false; }
        return CoreRange.TryCreate(first,last,out mapped)==SheetStatus.Ok;
    }

    private static bool TryMapRange(CellRange source,StructureAxis axis,int index,bool insert,out CoreRange mapped)
    {
        if(CellAddress.TryCreate(source.StartColumn,source.StartRow,out CellAddress first)!=SheetStatus.Ok ||
            CellAddress.TryCreate(source.EndColumn,source.EndRow,out CellAddress last)!=SheetStatus.Ok)
        { mapped=default; return false; }
        if(CoreRange.TryCreate(first,last,out CoreRange range)!=SheetStatus.Ok)
        { mapped=default; return false; }
        return TryMapRange(range,axis,index,insert,out mapped);
    }

    private bool TryAdjustFormulaReferences(string formula,StructureAxis axis,int index,bool insert,out string adjusted)
    {
        adjusted=formula;
        if(FormulaLexer.TryTokenize(formula,out IReadOnlyList<FormulaToken> tokens)!=SheetStatus.Ok) return false;
        List<(int Start,int Length,string Value)> replacements=new();
        HashSet<int> replacedTokens=new();
        for(int tokenIndex=0;tokenIndex<tokens.Count;++tokenIndex)
        {
            if(replacedTokens.Contains(tokenIndex)) continue;
            FormulaToken token=tokens[tokenIndex];
            if(token.Kind!=FormulaTokenKind.Reference) continue;
            string source=formula.Substring(token.Start,token.Length);
            int qualifier=source.LastIndexOf('!');
            if(qualifier>=0) continue;
            if(CellAddress.TryParse(source,out CellAddress address)!=SheetStatus.Ok) continue;
            int coordinate=axis==StructureAxis.Row?address.Row:address.Column;
            bool deleted=!insert && coordinate==index;
            if(!deleted && !((insert && coordinate>=index)||(!insert && coordinate>index))) continue;
            int newRow=address.Row,newColumn=address.Column;
            if(deleted)
            {
                FormulaToken? adjacent=tokenIndex>=2 && tokens[tokenIndex-1].Kind==FormulaTokenKind.Colon &&
                    tokens[tokenIndex-2].Kind==FormulaTokenKind.Reference ? tokens[tokenIndex-2] :
                    tokenIndex+2<tokens.Count && tokens[tokenIndex+1].Kind==FormulaTokenKind.Colon &&
                    tokens[tokenIndex+2].Kind==FormulaTokenKind.Reference ? tokens[tokenIndex+2] : null;
                int previous=PreviousNonSpaceToken(tokens,tokenIndex-1);
                int next=NextNonSpaceToken(tokens,tokenIndex+1);
                if(adjacent is null && previous>=0 && tokens[previous].Kind==FormulaTokenKind.Colon)
                {
                    int beforeColon=PreviousNonSpaceToken(tokens,previous-1);
                    if(beforeColon>=0 && tokens[beforeColon].Kind==FormulaTokenKind.Reference)
                        adjacent=tokens[beforeColon];
                }
                if(adjacent is null && next>=0 && tokens[next].Kind==FormulaTokenKind.Colon)
                {
                    int afterColon=NextNonSpaceToken(tokens,next+1);
                    if(afterColon>=0 && tokens[afterColon].Kind==FormulaTokenKind.Reference)
                        adjacent=tokens[afterColon];
                }
                if(adjacent is null)
                { replacements.Add((token.Start,token.Length,"#REF!")); continue; }
                string adjacentSource=formula.Substring(adjacent.Start,adjacent.Length);
                int adjacentQualifier=adjacentSource.LastIndexOf('!');
                if(adjacentQualifier>=0 || CellAddress.TryParse(adjacentSource,out CellAddress other)!=SheetStatus.Ok)
                    return false;
                int otherCoordinate=axis==StructureAxis.Row?other.Row:other.Column;
                if(otherCoordinate==index)
                {
                    int otherTokenIndex=-1;
                    for(int i=0;i<tokens.Count;++i)
                        if(ReferenceEquals(tokens[i],adjacent)) { otherTokenIndex=i; break; }
                    if(otherTokenIndex<0) return false;
                    FormulaToken first=tokens[Math.Min(tokenIndex,otherTokenIndex)];
                    FormulaToken last=tokens[Math.Max(tokenIndex,otherTokenIndex)];
                    replacements.Add((first.Start,last.Start+last.Length-first.Start,"#REF!"));
                    replacedTokens.Add(tokenIndex);
                    replacedTokens.Add(otherTokenIndex);
                    continue;
                }
                coordinate=otherCoordinate<index?index-1:index;
            }
            else coordinate+=insert?1:-1;
            if(axis==StructureAxis.Row) newRow=coordinate; else newColumn=coordinate;
            string original=source;
            int lettersStart=original.StartsWith('$')?1:0;
            int lettersEnd=lettersStart;
            while(lettersEnd<original.Length && char.IsAsciiLetter(original[lettersEnd])) ++lettersEnd;
            bool absoluteColumn=lettersStart==1;
            bool absoluteRow=lettersEnd<original.Length && original[lettersEnd]=='$';
            string label=ColumnName(newColumn);
            string replacement=(absoluteColumn?"$":"")+label+(absoluteRow?"$":"")+(newRow+1).ToString(CultureInfo.InvariantCulture);
            replacements.Add((token.Start,token.Length,replacement));
        }
        if(replacements.Count==0) return true;
        StringBuilder builder=new(formula.Length);
        int cursor=0;
        foreach(var replacement in replacements.OrderBy(item=>item.Start))
        {
            builder.Append(formula,cursor,replacement.Start-cursor);
            builder.Append(replacement.Value);
            cursor=replacement.Start+replacement.Length;
        }
        builder.Append(formula,cursor,formula.Length-cursor);
        adjusted=builder.ToString();
        return true;
    }

    private static int PreviousNonSpaceToken(IReadOnlyList<FormulaToken> tokens,int index)
    {
        while(index>=0 && tokens[index].Kind==FormulaTokenKind.Space) --index;
        return index;
    }

    private static int NextNonSpaceToken(IReadOnlyList<FormulaToken> tokens,int index)
    {
        while(index<tokens.Count && tokens[index].Kind==FormulaTokenKind.Space) ++index;
        return index<tokens.Count?index:-1;
    }

    private static void ShiftDimensions(Dictionary<int,double> dimensions,int index,bool insert,int limit)
    {
        var updated=new Dictionary<int,double>();
        foreach(var pair in dimensions)
        {
            if(!insert && pair.Key==index) continue;
            int key=(insert && pair.Key>=index)||(!insert && pair.Key>index)?pair.Key+(insert?1:-1):pair.Key;
            if(key<limit) updated[key]=pair.Value;
        }
        dimensions.Clear();
        foreach(var pair in updated) dimensions.Add(pair.Key,pair.Value);
    }
}
