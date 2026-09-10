using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.Numerics;
using System.Text;
using KHZ.Sheet.Core;

namespace KHZ.Sheet.Desktop;

public sealed class WorkbookSession : IDisposable
{
    private int _nextSheetNumber = 1;

    public WorkbookSession()
    {
        Sheets = new ObservableCollection<WorksheetSession>();
        AddSheet();
    }

    public ObservableCollection<WorksheetSession> Sheets { get; }

    public WorksheetSession AddSheet(string? requestedName = null)
    {
        string name = string.IsNullOrWhiteSpace(requestedName)
            ? NextAvailableName()
            : MakeUniqueName(requestedName.Trim());

        WorksheetSession sheet = new(name);
        Sheets.Add(sheet);
        return sheet;
    }

    private string NextAvailableName()
    {
        while (true)
        {
            string candidate = $"Sheet{_nextSheetNumber++}";
            if (!Sheets.Any(s => string.Equals(s.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }
    }

    private string MakeUniqueName(string requested)
    {
        if (!Sheets.Any(s => string.Equals(s.Name, requested, StringComparison.OrdinalIgnoreCase)))
        {
            return requested;
        }

        int suffix = 2;
        while (Sheets.Any(s => string.Equals(s.Name, $"{requested} {suffix}", StringComparison.OrdinalIgnoreCase)))
        {
            suffix++;
        }

        return $"{requested} {suffix}";
    }

    public void Dispose()
    {
        foreach (WorksheetSession sheet in Sheets)
        {
            sheet.Dispose();
        }

        Sheets.Clear();
    }
}

public sealed class WorksheetSession : IDisposable
{
    private const int DefaultRows = 256;
    private const int DefaultColumns = 52;
    private static readonly nuint ArenaBytes = (nuint)(64 * 1024 * 1024);
    private static readonly nuint CellCapacity = (nuint)200_000;

    private readonly Dictionary<long, string> _inputs = new();
    private NativeSheet? _native;
    private bool _disposed;

    public WorksheetSession(string name)
    {
        Name = name;
        Grid = CreateGrid(DefaultRows, DefaultColumns);

        try
        {
            EngineStatus = NativeSheet.TryCreate(ArenaBytes, CellCapacity, out _native);
        }
        catch (DllNotFoundException)
        {
            EngineStatus = SheetStatus.ErrState;
        }
        catch (BadImageFormatException)
        {
            EngineStatus = SheetStatus.ErrState;
        }
        catch (EntryPointNotFoundException)
        {
            EngineStatus = SheetStatus.ErrUnsupported;
        }
    }

    public string Name { get; }

    public DataTable Grid { get; }

    public SheetStatus EngineStatus { get; private set; }

    public bool EngineAvailable => EngineStatus == SheetStatus.Ok && _native is not null;

    public nuint NativeCellCount => _native?.CellCount ?? 0;

    public ulong NativeCommits => _native?.Commits ?? 0UL;

    public string EngineSummary =>
        EngineAvailable
            ? $"native · exact · cells {NativeCellCount:N0} · commits {NativeCommits:N0}"
            : $"managed view · {SheetStatusText.Name(EngineStatus)}";

    public string GetInput(int row, int column)
    {
        if (!InBounds(row, column))
        {
            return string.Empty;
        }

        long key = Key(row, column);
        if (_inputs.TryGetValue(key, out string? input))
        {
            return input;
        }

        return Grid.Rows[row][column]?.ToString() ?? string.Empty;
    }

    public bool CommitCell(int row, int column, string? input, out string message)
    {
        message = string.Empty;

        if (!InBounds(row, column))
        {
            message = SheetStatusText.Name(SheetStatus.ErrRange);
            return false;
        }

        string raw = input ?? string.Empty;
        _inputs[Key(row, column)] = raw;

        if (!EngineAvailable)
        {
            Grid.Rows[row][column] = raw;
            message = EngineSummary;
            return true;
        }

        SheetStatus status;
        ulong evaluated = 0UL;

        if (raw.StartsWith("=", StringComparison.Ordinal))
        {
            status = InstallFormula((uint)column, (uint)row, raw, out string formulaError);
            if (status != SheetStatus.Ok)
            {
                Grid.Rows[row][column] = raw;
                message = formulaError;
                return false;
            }

            status = _native!.TryRecalculate(out evaluated);
            if (status != SheetStatus.Ok)
            {
                Grid.Rows[row][column] = raw;
                message = SheetStatusText.Name(status);
                return false;
            }
        }
        else
        {
            status = StoreLiteral((uint)column, (uint)row, raw);
            if (status != SheetStatus.Ok)
            {
                Grid.Rows[row][column] = raw;
                message = SheetStatusText.Name(status);
                return false;
            }

            status = _native!.TryRecalculate(out evaluated);
            if (status != SheetStatus.Ok && status != SheetStatus.ErrMissing)
            {
                Grid.Rows[row][column] = raw;
                message = SheetStatusText.Name(status);
                return false;
            }
        }

        RefreshComputedCells();
        message = evaluated == 0UL
            ? EngineSummary
            : $"recalculated {evaluated:N0} · {EngineSummary}";
        return true;
    }

    public bool Recalculate(out string message)
    {
        if (!EngineAvailable)
        {
            message = EngineSummary;
            return false;
        }

        SheetStatus status = _native!.TryRecalculate(out ulong evaluated);
        if (status != SheetStatus.Ok)
        {
            message = SheetStatusText.Name(status);
            return false;
        }

        RefreshComputedCells();
        message = $"recalculated {evaluated:N0} · {EngineSummary}";
        return true;
    }

    public bool Verify(out string message)
    {
        if (!EngineAvailable)
        {
            message = EngineSummary;
            return false;
        }

        SheetStatus status = _native!.VerifyChain(out nuint failedIndex);
        if (status != SheetStatus.Ok)
        {
            message = $"{SheetStatusText.Name(status)} · link {failedIndex}";
            return false;
        }

        status = _native.TryProofHex(out string proof);
        if (status != SheetStatus.Ok)
        {
            message = SheetStatusText.Name(status);
            return false;
        }

        string head = proof.Length > 16 ? proof[..16] : proof;
        message = $"proof verified · {head} · commits {NativeCommits:N0}";
        return true;
    }

    public bool ExportXlsx(string path, out string message)
    {
        message = string.Empty;

        if (!EngineAvailable)
        {
            message = EngineSummary;
            return false;
        }

        SheetStatus status;
        XlsxReport report;

        try
        {
            status = _native!.TryWriteXlsx(path, Name, out report);
        }
        catch (DllNotFoundException)
        {
            message = SheetStatusText.Name(SheetStatus.ErrState);
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            message = SheetStatusText.Name(SheetStatus.ErrUnsupported);
            return false;
        }

        if (status != SheetStatus.Ok)
        {
            message = SheetStatusText.Name(status);
            return false;
        }

        message = $"xlsx · {report.CellsWritten:N0} cells · {report.BytesWritten:N0} bytes · lossy {report.LossyCells:N0}";
        return true;
    }

    public void SaveCsv(string path)
    {
        using StreamWriter writer = new(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        int lastRow = LastPopulatedRow();
        int lastColumn = LastPopulatedColumn();

        if (lastRow < 0 || lastColumn < 0)
        {
            return;
        }

        for (int row = 0; row <= lastRow; row++)
        {
            for (int column = 0; column <= lastColumn; column++)
            {
                if (column > 0)
                {
                    writer.Write(',');
                }

                writer.Write(EscapeCsv(GetInput(row, column)));
            }

            writer.WriteLine();
        }
    }

    public bool LoadCsv(string path, out string message)
    {
        message = string.Empty;
        string text = File.ReadAllText(path);
        List<List<string>> records;

        try
        {
            records = ParseCsv(text);
        }
        catch (FormatException ex)
        {
            message = ex.Message;
            return false;
        }

        int rowCount = Math.Min(records.Count, Grid.Rows.Count);
        int imported = 0;

        for (int row = 0; row < rowCount; row++)
        {
            int columnCount = Math.Min(records[row].Count, Grid.Columns.Count);
            for (int column = 0; column < columnCount; column++)
            {
                string value = records[row][column];
                if (value.Length == 0)
                {
                    continue;
                }

                CommitCell(row, column, value, out _);
                imported++;
            }
        }

        message = $"csv · {imported:N0} populated cells";
        return true;
    }

    public void RefreshComputedCells()
    {
        if (!EngineAvailable)
        {
            return;
        }

        foreach (KeyValuePair<long, string> pair in _inputs)
        {
            DecodeKey(pair.Key, out int row, out int column);
            Grid.Rows[row][column] = RenderCell(row, column, pair.Value);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _native?.Dispose();
        _native = null;
        _disposed = true;
    }

    public static string ColumnName(int zeroBasedColumn)
    {
        if (zeroBasedColumn < 0)
        {
            return string.Empty;
        }

        StringBuilder builder = new();
        int value = zeroBasedColumn + 1;

        while (value > 0)
        {
            value--;
            builder.Insert(0, (char)('A' + (value % 26)));
            value /= 26;
        }

        return builder.ToString();
    }

    private SheetStatus InstallFormula(uint column, uint row, string source, out string message)
    {
        FormulaParseFailure failure;
        SheetStatus status = _native!.TrySetFormula(column, row, source, out failure);

        if (status != SheetStatus.Ok && source.Length > 1)
        {
            status = _native.TrySetFormula(column, row, source[1..], out failure);
        }

        message = status == SheetStatus.Ok
            ? string.Empty
            : $"{SheetStatusText.Name(status)} · byte {failure.Offset} · {failure.Expected}";

        return status;
    }

    private SheetStatus StoreLiteral(uint column, uint row, string raw)
    {
        string value = raw.Trim();

        if (value.Length == 0)
        {
            return _native!.SetText(column, row, ReadOnlySpan<byte>.Empty);
        }

        if (string.Equals(value, "TRUE", StringComparison.OrdinalIgnoreCase))
        {
            return _native!.SetBool(column, row, true);
        }

        if (string.Equals(value, "FALSE", StringComparison.OrdinalIgnoreCase))
        {
            return _native!.SetBool(column, row, false);
        }

        if (TryCellError(value, out CellErrorCode error))
        {
            return _native!.SetError(column, row, error);
        }

        SheetStatus numericStatus = TryParseExactNumber(value, out long numerator, out long denominator, out bool numeric);
        if (numeric)
        {
            if (numericStatus != SheetStatus.Ok)
            {
                return numericStatus;
            }

            return denominator == 1L
                ? _native!.SetInt64(column, row, numerator)
                : _native!.SetRational(column, row, numerator, denominator);
        }

        return _native!.SetText(column, row, raw);
    }

    private string RenderCell(int row, int column, string fallback)
    {
        SheetStatus status = _native!.TryGetCell((uint)column, (uint)row, out KhzCellNative cell);
        if (status != SheetStatus.Ok)
        {
            return fallback;
        }

        if (cell.ErrorCode != CellErrorCode.None)
        {
            return ErrorText(cell.ErrorCode);
        }

        switch (cell.Kind)
        {
            case CellKind.Empty:
                return string.Empty;

            case CellKind.Rational:
            case CellKind.Formula:
                return cell.Value.ToString();

            case CellKind.Bool:
                return cell.BoolValue != 0u ? "TRUE" : "FALSE";

            case CellKind.Error:
                return ErrorText(cell.ErrorCode);

            case CellKind.Text:
            {
                status = _native.TryGetTextBytes((uint)column, (uint)row, out ReadOnlySpan<byte> utf8);
                return status == SheetStatus.Ok ? Encoding.UTF8.GetString(utf8) : fallback;
            }

            default:
                return fallback;
        }
    }

    private static DataTable CreateGrid(int rows, int columns)
    {
        DataTable table = new("KHZ Sheet");

        for (int column = 0; column < columns; column++)
        {
            table.Columns.Add(ColumnName(column), typeof(string));
        }

        for (int row = 0; row < rows; row++)
        {
            DataRow created = table.NewRow();
            for (int column = 0; column < columns; column++)
            {
                created[column] = string.Empty;
            }

            table.Rows.Add(created);
        }

        return table;
    }

    private bool InBounds(int row, int column) =>
        row >= 0 && row < Grid.Rows.Count && column >= 0 && column < Grid.Columns.Count;

    private int LastPopulatedRow()
    {
        int last = -1;
        foreach (long key in _inputs.Keys)
        {
            DecodeKey(key, out int row, out _);
            last = Math.Max(last, row);
        }

        return last;
    }

    private int LastPopulatedColumn()
    {
        int last = -1;
        foreach (long key in _inputs.Keys)
        {
            DecodeKey(key, out _, out int column);
            last = Math.Max(last, column);
        }

        return last;
    }

    private static long Key(int row, int column) => ((long)row << 32) | (uint)column;

    private static void DecodeKey(long key, out int row, out int column)
    {
        row = (int)(key >> 32);
        column = (int)(key & 0xFFFFFFFFL);
    }

    private static bool TryCellError(string value, out CellErrorCode error)
    {
        switch (value.ToUpperInvariant())
        {
            case "#NULL!":
                error = CellErrorCode.Null;
                return true;
            case "#DIV/0!":
                error = CellErrorCode.Div0;
                return true;
            case "#VALUE!":
                error = CellErrorCode.Value;
                return true;
            case "#REF!":
                error = CellErrorCode.Ref;
                return true;
            case "#NAME?":
                error = CellErrorCode.Name;
                return true;
            case "#NUM!":
                error = CellErrorCode.Num;
                return true;
            case "#N/A":
                error = CellErrorCode.Na;
                return true;
            default:
                error = CellErrorCode.None;
                return false;
        }
    }

    private static string ErrorText(CellErrorCode error) =>
        error switch
        {
            CellErrorCode.Null => "#NULL!",
            CellErrorCode.Div0 => "#DIV/0!",
            CellErrorCode.Value => "#VALUE!",
            CellErrorCode.Ref => "#REF!",
            CellErrorCode.Name => "#NAME?",
            CellErrorCode.Num => "#NUM!",
            CellErrorCode.Na => "#N/A",
            _ => "#ERROR!"
        };

    private static SheetStatus TryParseExactNumber(
        string text,
        out long numerator,
        out long denominator,
        out bool recognized)
    {
        numerator = 0L;
        denominator = 1L;
        recognized = false;

        int slash = text.IndexOf('/');
        if (slash > 0 && slash == text.LastIndexOf('/'))
        {
            string left = text[..slash].Trim();
            string right = text[(slash + 1)..].Trim();

            if (long.TryParse(left, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) &&
                long.TryParse(right, NumberStyles.Integer, CultureInfo.InvariantCulture, out long d))
            {
                recognized = true;
                if (d == 0L)
                {
                    return SheetStatus.ErrDivZero;
                }

                BigInteger bn = n;
                BigInteger bd = d;
                Normalize(ref bn, ref bd);

                if (bn < long.MinValue || bn > long.MaxValue || bd < 1 || bd > long.MaxValue)
                {
                    return SheetStatus.ErrOverflow;
                }

                numerator = (long)bn;
                denominator = (long)bd;
                return SheetStatus.Ok;
            }

            return SheetStatus.Ok;
        }

        int i = 0;
        bool negative = false;

        if (i < text.Length && (text[i] == '+' || text[i] == '-'))
        {
            negative = text[i] == '-';
            i++;
        }

        StringBuilder digits = new();
        bool hasDigit = false;
        bool seenPoint = false;
        int fractionDigits = 0;

        while (i < text.Length)
        {
            char c = text[i];

            if (char.IsAsciiDigit(c))
            {
                digits.Append(c);
                hasDigit = true;
                if (seenPoint)
                {
                    fractionDigits++;
                }

                i++;
                continue;
            }

            if (c == '.' && !seenPoint)
            {
                seenPoint = true;
                i++;
                continue;
            }

            break;
        }

        if (!hasDigit)
        {
            return SheetStatus.Ok;
        }

        int exponent = 0;
        if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
        {
            i++;

            bool exponentNegative = false;
            if (i < text.Length && (text[i] == '+' || text[i] == '-'))
            {
                exponentNegative = text[i] == '-';
                i++;
            }

            int start = i;
            while (i < text.Length && char.IsAsciiDigit(text[i]))
            {
                if (exponent > 10000)
                {
                    recognized = true;
                    return SheetStatus.ErrOverflow;
                }

                exponent = checked((exponent * 10) + (text[i] - '0'));
                i++;
            }

            if (i == start)
            {
                return SheetStatus.Ok;
            }

            if (exponentNegative)
            {
                exponent = -exponent;
            }
        }

        if (i != text.Length)
        {
            return SheetStatus.Ok;
        }

        recognized = true;

        if (Math.Abs((long)exponent - fractionDigits) > 256L)
        {
            return SheetStatus.ErrOverflow;
        }

        BigInteger parsed = BigInteger.Parse(digits.ToString(), CultureInfo.InvariantCulture);
        if (negative)
        {
            parsed = -parsed;
        }

        int scale = fractionDigits - exponent;
        BigInteger den = BigInteger.One;

        if (scale > 0)
        {
            den = BigInteger.Pow(10, scale);
        }
        else if (scale < 0)
        {
            parsed *= BigInteger.Pow(10, -scale);
        }

        Normalize(ref parsed, ref den);

        if (parsed < long.MinValue || parsed > long.MaxValue || den < 1 || den > long.MaxValue)
        {
            return SheetStatus.ErrOverflow;
        }

        numerator = (long)parsed;
        denominator = (long)den;
        return SheetStatus.Ok;
    }

    private static void Normalize(ref BigInteger numerator, ref BigInteger denominator)
    {
        if (denominator.Sign < 0)
        {
            numerator = -numerator;
            denominator = -denominator;
        }

        BigInteger gcd = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
        if (gcd > BigInteger.One)
        {
            numerator /= gcd;
            denominator /= gcd;
        }
    }

    private static string EscapeCsv(string value)
    {
        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static List<List<string>> ParseCsv(string text)
    {
        List<List<string>> rows = new();
        List<string> row = new();
        StringBuilder field = new();

        bool quoted = false;
        int i = 0;

        while (i < text.Length)
        {
            char c = text[i];

            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                        continue;
                    }

                    quoted = false;
                    i++;
                    continue;
                }

                field.Append(c);
                i++;
                continue;
            }

            if (c == '"' && field.Length == 0)
            {
                quoted = true;
                i++;
                continue;
            }

            if (c == ',')
            {
                row.Add(field.ToString());
                field.Clear();
                i++;
                continue;
            }

            if (c == '\r' || c == '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = new List<string>();

                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i += 2;
                }
                else
                {
                    i++;
                }

                continue;
            }

            field.Append(c);
            i++;
        }

        if (quoted)
        {
            throw new FormatException("CSV contains an unterminated quoted field.");
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }
}
