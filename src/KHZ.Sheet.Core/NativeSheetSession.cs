using System;
using System.Runtime.InteropServices;
using System.Text;

namespace KHZ.Sheet.Core;

public sealed unsafe class NativeSheetSession : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly object _gate = new();
    private readonly SafeSheetHandle _handle;
    private bool _disposed;

    private NativeSheetSession(SafeSheetHandle handle) => _handle = handle;

    public static SheetStatus TryCreate(nuint arenaBytes, nuint cellCapacity,
        out NativeSheetSession? session)
    {
        session = null;
        SafeSheetHandle? handle = null;
        try
        {
            SheetStatus status = ValidateAbi(out nuint bytes, out nuint alignment);
            if (status != SheetStatus.Ok) return status;
            handle = SafeSheetHandle.Allocate(bytes, alignment);
            status = NativeMethods.SheetInit(handle, arenaBytes, cellCapacity);
            if (status != SheetStatus.Ok) return status;
            handle.MarkInitialized();
            session = new NativeSheetSession(handle);
            handle = null;
            return SheetStatus.Ok;
        }
        catch (DllNotFoundException) { return SheetStatus.ErrMissing; }
        catch (EntryPointNotFoundException) { return SheetStatus.ErrUnsupported; }
        catch (BadImageFormatException) { return SheetStatus.ErrUnsupported; }
        catch (OutOfMemoryException) { return SheetStatus.ErrMemory; }
        catch (OverflowException) { return SheetStatus.ErrOverflow; }
        finally { handle?.Dispose(); }
    }

    private static SheetStatus ValidateAbi(out nuint bytes, out nuint alignment)
    {
        bytes = alignment = 0;
        if (NativeMethods.Version() != NativeMethods.AbiVersion) return SheetStatus.ErrUnsupported;
        SheetStatus status = NativeMethods.Sizes(out KhzAbiSizes sizes);
        if (status != SheetStatus.Ok) return status;
        bytes = NativeMethods.SheetBytes();
        alignment = NativeMethods.SheetAlignment();
        if (sizes.Version != NativeMethods.AbiVersion || sizes.PointerBytes != sizeof(nint) ||
            sizes.SizeTBytes != sizeof(nuint) || sizes.Sha256DigestBytes != 32 ||
            sizes.CellBytes != sizeof(KhzCellNative) || sizes.RationalBytes != sizeof(KhzRationalNative) ||
            sizes.SheetBytes != bytes || bytes == 0 || bytes > 1024 * 1024 ||
            alignment == 0 || alignment > 1024 * 1024 || (alignment & (alignment - 1)) != 0 ||
            NativeMethods.ParseErrorBytes() != (nuint)sizeof(KhzFormulaParseErrorNative) ||
            NativeMethods.ChainAuditBytes() != (nuint)sizeof(NativeChainAudit) ||
            NativeMethods.XlsxReportBytes() != (nuint)sizeof(KhzXlsxReportNative))
            return SheetStatus.ErrUnsupported;

        string[] fields = { nameof(KhzCellNative.Col), nameof(KhzCellNative.Row),
            nameof(KhzCellNative.KindRaw), nameof(KhzCellNative.ErrorRaw), nameof(KhzCellNative.Value),
            nameof(KhzCellNative.Text), nameof(KhzCellNative.Formula), nameof(KhzCellNative.TextLen),
            nameof(KhzCellNative.FormulaLen), nameof(KhzCellNative.BoolValue), nameof(KhzCellNative.Flags),
            nameof(KhzCellNative.Revision), nameof(KhzCellNative.Proof), nameof(KhzCellNative.Reserved) };
        for (uint i = 0; i < fields.Length; ++i)
            if (NativeMethods.CellFieldOffset(i) != (nuint)Marshal.OffsetOf<KhzCellNative>(fields[i]))
                return SheetStatus.ErrUnsupported;
        if (alignment < 64) alignment = 64;
        return SheetStatus.Ok;
    }

    public SheetStatus SetInt64(uint col, uint row, long value)
    {
        lock (_gate)
            return _disposed ? SheetStatus.ErrState : NativeMethods.SetInt64(_handle, col, row, value);
    }

    public SheetStatus SetRational(uint col, uint row, long numerator, long denominator)
    {
        lock (_gate)
        {
            if (_disposed) return SheetStatus.ErrState;
            SheetStatus status = NativeMethods.RationalMake(numerator, denominator, out KhzRationalNative value);
            return status == SheetStatus.Ok ? NativeMethods.SetRational(_handle, col, row, value) : status;
        }
    }

    public SheetStatus SetBool(uint col, uint row, bool value)
    {
        lock (_gate)
            return _disposed ? SheetStatus.ErrState : NativeMethods.SetBool(_handle, col, row, value ? 1 : 0);
    }

    public SheetStatus SetError(uint col, uint row, CellErrorCode error)
    {
        lock (_gate)
            return _disposed ? SheetStatus.ErrState : NativeMethods.SetError(_handle, col, row, error);
    }

    public SheetStatus SetText(uint col, uint row, string text)
    {
        if (text is null) return SheetStatus.ErrNull;
        try { return SetText(col, row, Utf8.GetBytes(text)); }
        catch (EncoderFallbackException) { return SheetStatus.ErrFormat; }
    }

    public SheetStatus SetText(uint col, uint row, ReadOnlySpan<byte> utf8)
    {
        lock (_gate)
        {
            if (_disposed) return SheetStatus.ErrState;
            try { _ = Utf8.GetCharCount(utf8); }
            catch (DecoderFallbackException) { return SheetStatus.ErrFormat; }
            fixed (byte* text = utf8)
                return NativeMethods.SetText(_handle, col, row, text, (nuint)utf8.Length);
        }
    }

    // The transactional native installer invokes khz_sheet_set_formula after
    // parsing and dependency preparation. Calling the raw setter alone would
    // not install dependency edges, so the session never exposes that shortcut.
    public SheetStatus TrySetFormula(uint col, uint row, string source, out FormulaParseFailure failure)
    {
        failure = new FormulaParseFailure(0, string.Empty);
        if (source is null) return SheetStatus.ErrNull;
        lock (_gate)
        {
            if (_disposed) return SheetStatus.ErrState;
            byte[] bytes;
            int count;
            try
            {
                count = Utf8.GetByteCount(source);
                if (count > 8192) return SheetStatus.ErrLimit;
                bytes = new byte[count + 1];
                Utf8.GetBytes(source.AsSpan(), bytes.AsSpan(0, count));
            }
            catch (EncoderFallbackException) { return SheetStatus.ErrFormat; }
            KhzFormulaParseErrorNative error = default;
            SheetStatus status;
            fixed (byte* text = bytes)
                status = NativeMethods.InstallFormula(_handle, col, row, text, (nuint)count, &error);
            int length = 0;
            while (length < 32 && error.Expected[length] != 0) ++length;
            failure = new FormulaParseFailure((ulong)error.Offset, Encoding.UTF8.GetString(error.Expected, length));
            return status;
        }
    }

    // A failure can follow earlier successful cell commits. evaluated is the
    // number actually committed; a failed recalculation is not a rollback.
    public SheetStatus TryRecalculate(out ulong evaluated)
    {
        lock (_gate)
        {
            evaluated = 0;
            return _disposed ? SheetStatus.ErrState : NativeMethods.Recalculate(_handle, out evaluated);
        }
    }

    public SheetStatus TryGetCell(uint col, uint row, out KhzCellNative cell)
    {
        lock (_gate)
        {
            cell = default;
            if (_disposed) return SheetStatus.ErrState;
            try
            {
                SheetStatus status = NativeMethods.GetCell(_handle, col, row, out IntPtr address);
                if (status != SheetStatus.Ok) return status;
                if (address == IntPtr.Zero) return SheetStatus.ErrState;
                cell = *(KhzCellNative*)address;
                // Never let a borrowed native pointer escape this locked copy.
                cell.Text = cell.Formula = IntPtr.Zero;
                return SheetStatus.Ok;
            }
            finally { GC.KeepAlive(_handle); }
        }
    }

    public SheetStatus TryGetTextBytes(uint col, uint row, out ReadOnlySpan<byte> utf8)
    {
        lock (_gate)
        {
            utf8 = default;
            if (_disposed) return SheetStatus.ErrState;
            try
            {
                SheetStatus status = NativeMethods.GetCell(_handle, col, row, out IntPtr address);
                if (status != SheetStatus.Ok) return status;
                if (address == IntPtr.Zero) return SheetStatus.ErrState;
                KhzCellNative cell = *(KhzCellNative*)address;
                if (cell.Kind != CellKind.Text) return SheetStatus.ErrType;
                if (cell.TextLen == 0) { utf8 = ReadOnlySpan<byte>.Empty; return SheetStatus.Ok; }
                if (cell.Text == IntPtr.Zero || cell.TextLen > int.MaxValue) return SheetStatus.ErrState;
                // This managed copy remains valid after further edits or disposal.
                utf8 = new ReadOnlySpan<byte>((void*)cell.Text, (int)cell.TextLen).ToArray();
                return SheetStatus.Ok;
            }
            finally { GC.KeepAlive(_handle); }
        }
    }

    public SheetStatus VerifyChain(out nuint failedIndex)
    {
        lock (_gate)
        {
            failedIndex = 0;
            return _disposed ? SheetStatus.ErrState : NativeMethods.VerifyChain(_handle, out failedIndex);
        }
    }

    public SheetStatus TryAudit(out NativeChainAudit audit)
    {
        lock (_gate)
        {
            audit = default;
            return _disposed ? SheetStatus.ErrState : NativeMethods.AuditChain(_handle, out audit);
        }
    }

    public SheetStatus TryProofHex(out string hex)
    {
        lock (_gate)
        {
            hex = string.Empty;
            if (_disposed) return SheetStatus.ErrState;
            byte* buffer = stackalloc byte[65];
            new Span<byte>(buffer, 65).Clear();
            SheetStatus status = NativeMethods.ProofHex(_handle, buffer);
            if (status != SheetStatus.Ok) return status;
            if (buffer[64] != 0) return SheetStatus.ErrFormat;
            for (int i = 0; i < 64; ++i)
                if (!(buffer[i] >= '0' && buffer[i] <= '9') && !(buffer[i] >= 'a' && buffer[i] <= 'f'))
                    return SheetStatus.ErrFormat;
            hex = Encoding.ASCII.GetString(buffer, 64);
            return SheetStatus.Ok;
        }
    }

    public nuint CellCount
    {
        get { lock (_gate) return _disposed ? 0 : NativeMethods.CellCount(_handle); }
    }

    public ulong Commits
    {
        get { lock (_gate) return _disposed ? 0 : NativeMethods.Commits(_handle); }
    }

    public SheetStatus TryWriteXlsx(string path, string? sheetName, out XlsxReport report)
    {
        report = default;
        if (string.IsNullOrEmpty(path)) return SheetStatus.ErrRange;
        if (path.IndexOf('\0') >= 0 || sheetName?.IndexOf('\0') >= 0) return SheetStatus.ErrFormat;
        lock (_gate)
        {
            if (_disposed) return SheetStatus.ErrState;
            byte[] pathBytes;
            byte[]? nameBytes;
            try
            {
                if (Utf8.GetByteCount(path) >= 4096) return SheetStatus.ErrRange;
                pathBytes = Utf8.GetBytes(path + "\0");
                nameBytes = sheetName is null ? null : Utf8.GetBytes(sheetName + "\0");
            }
            catch (EncoderFallbackException) { return SheetStatus.ErrFormat; }
            IntPtr scratch = NativeMethods.SheetArena(_handle);
            if (scratch == IntPtr.Zero) return SheetStatus.ErrState;
            fixed (byte* p = pathBytes)
            fixed (byte* name = nameBytes)
            {
                SheetStatus status = NativeMethods.WriteXlsx(_handle, scratch, p, name, out KhzXlsxReportNative native);
                if (status == SheetStatus.Ok)
                    report = new XlsxReport(native.CellsWritten, native.SharedStrings, native.FormulaCells,
                        native.LossyCells, native.BytesWritten, native.ArenaBytesUsed);
                return status;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _handle.Dispose();
        }
    }
}
