using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KHZ.Sheet.Core;

[StructLayout(LayoutKind.Sequential)]
public struct NativeChainAudit
{
    public ulong EntriesExamined;
    public ulong CellsVerified;
    public ulong Superseded;
    public ulong Dropped;
    public nuint FailedEntry;
}

internal sealed unsafe class SafeSheetHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private bool _initialized;
    private SafeSheetHandle() : base(true) { }

    internal static SafeSheetHandle Allocate(nuint bytes, nuint alignment)
    {
        SafeSheetHandle result = new();
        try
        {
            nuint rounded = checked(bytes + alignment - 1) & ~(alignment - 1);
            void* block = NativeMemory.AlignedAlloc(rounded, alignment);
            if (block == null) throw new OutOfMemoryException();
            result.SetHandle((IntPtr)block);
            NativeMemory.Clear(block, rounded);
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    internal void MarkInitialized() => _initialized = true;

    protected override bool ReleaseHandle()
    {
        try
        {
            if (_initialized) NativeMethods.SheetDestroy(handle);
        }
        finally
        {
            NativeMemory.AlignedFree((void*)handle);
            handle = IntPtr.Zero;
        }
        return true;
    }
}

internal static unsafe class NativeMethods
{
    internal const string LibraryName = "khzcore";
    internal const uint AbiVersion = 96;
    private const CallingConvention Convention = CallingConvention.Cdecl;

    [DllImport(LibraryName, EntryPoint = "khz_abi_version", CallingConvention = Convention, ExactSpelling = true)]
    internal static extern uint Version();
    [DllImport(LibraryName, EntryPoint = "khz_abi_sizes", CallingConvention = Convention, ExactSpelling = true)]
    internal static extern SheetStatus Sizes(out KhzAbiSizes sizes);
    [DllImport(LibraryName, EntryPoint = "khz_abi_sheet_bytes", CallingConvention = Convention, ExactSpelling = true)]
    internal static extern nuint SheetBytes();
    [DllImport(LibraryName, EntryPoint = "khz_abi_sheet_alignment", CallingConvention = Convention, ExactSpelling = true)]
    internal static extern nuint SheetAlignment();
    [DllImport(LibraryName, EntryPoint = "khz_abi_cell_field_offset", CallingConvention = Convention, ExactSpelling = true)]
    internal static extern nuint CellFieldOffset(uint field);
    [DllImport(LibraryName, EntryPoint = "khz_abi_parse_error_bytes", CallingConvention = Convention, ExactSpelling = true)]
    internal static extern nuint ParseErrorBytes();
    [DllImport(LibraryName, EntryPoint = "khz_abi_chain_audit_bytes", CallingConvention = Convention, ExactSpelling = true)]
    internal static extern nuint ChainAuditBytes();
    [DllImport(LibraryName, EntryPoint = "khz_abi_xlsx_report_bytes", CallingConvention = Convention, ExactSpelling = true)]
    internal static extern nuint XlsxReportBytes();

    [DllImport(LibraryName, EntryPoint = "khz_sheet_init", CallingConvention = Convention, ExactSpelling = true)]
    internal static extern SheetStatus SheetInit(SafeSheetHandle sheet, nuint arenaBytes, nuint cellCapacity);
    [DllImport(LibraryName, EntryPoint = "khz_sheet_destroy", CallingConvention = Convention, ExactSpelling = true)]
    internal static extern void SheetDestroy(IntPtr sheet);
    [DllImport(LibraryName, EntryPoint = "khz_sheet_set_i64", CallingConvention = Convention, ExactSpelling = true)]
    internal static extern SheetStatus SetInt64(SafeSheetHandle sheet, uint col, uint row, long value);
    [DllImport(LibraryName, EntryPoint = "khz_rational_make", CallingConvention = Convention, ExactSpelling = true)]
    internal static extern SheetStatus RationalMake(long numerator, long denominator, out KhzRationalNative value);
    [DllImport(LibraryName, EntryPoint = "khz_sheet_set_rational", CallingConvention = Convention, ExactSpelling = true)]
    internal static extern SheetStatus SetRational(SafeSheetHandle sheet, uint col, uint row, KhzRationalNative value);
    [DllImport(LibraryName, EntryPoint = "khz_sheet_set_text", CallingConvention = Convention, ExactSpelling = true)]
    internal static extern SheetStatus SetText(SafeSheetHandle sheet, uint col, uint row, byte* text, nuint length);
    [DllImport(LibraryName, EntryPoint = "khz_sheet_set_bool", CallingConvention = Convention, ExactSpelling = true)]
    internal static extern SheetStatus SetBool(SafeSheetHandle sheet, uint col, uint row, int value);
    [DllImport(LibraryName, EntryPoint = "khz_sheet_set_error", CallingConvention = Convention, ExactSpelling = true)]
    internal static extern SheetStatus SetError(SafeSheetHandle sheet, uint col, uint row, CellErrorCode error);
    [DllImport(LibraryName, EntryPoint = "khz_sheet_set_formula", CallingConvention = Convention, ExactSpelling = true)]
    internal static extern SheetStatus SetFormulaSource(SafeSheetHandle sheet, uint col, uint row, byte* source, nuint length);
    [DllImport(LibraryName, EntryPoint = "khz_formula_set", CallingConvention = Convention, ExactSpelling = true)]
    internal static extern SheetStatus InstallFormula(SafeSheetHandle sheet, uint col, uint row, byte* source,
        nuint length, KhzFormulaParseErrorNative* error);
    [DllImport(LibraryName, EntryPoint = "khz_formula_recalc", CallingConvention = Convention, ExactSpelling = true)]
    internal static extern SheetStatus Recalculate(SafeSheetHandle sheet, out ulong evaluated);
    [DllImport(LibraryName, EntryPoint = "khz_sheet_get", CallingConvention = Convention, ExactSpelling = true)]
    internal static extern SheetStatus GetCell(SafeSheetHandle sheet, uint col, uint row, out IntPtr cell);
    [DllImport(LibraryName, EntryPoint = "khz_sheet_verify_chain", CallingConvention = Convention, ExactSpelling = true)]
    internal static extern SheetStatus VerifyChain(SafeSheetHandle sheet, out nuint failedIndex);
    [DllImport(LibraryName, EntryPoint = "khz_sheet_audit_chain", CallingConvention = Convention, ExactSpelling = true)]
    internal static extern SheetStatus AuditChain(SafeSheetHandle sheet, out NativeChainAudit audit);
    [DllImport(LibraryName, EntryPoint = "khz_sheet_proof_hex", CallingConvention = Convention, ExactSpelling = true)]
    internal static extern SheetStatus ProofHex(SafeSheetHandle sheet, byte* destination);
    [DllImport(LibraryName, EntryPoint = "khz_sheet_cell_count", CallingConvention = Convention, ExactSpelling = true)]
    internal static extern nuint CellCount(SafeSheetHandle sheet);
    [DllImport(LibraryName, EntryPoint = "khz_sheet_commits", CallingConvention = Convention, ExactSpelling = true)]
    internal static extern ulong Commits(SafeSheetHandle sheet);
    [DllImport(LibraryName, EntryPoint = "khz_sheet_arena", CallingConvention = Convention, ExactSpelling = true)]
    internal static extern IntPtr SheetArena(SafeSheetHandle sheet);
    [DllImport(LibraryName, EntryPoint = "khz_xlsx_write", CallingConvention = Convention, ExactSpelling = true)]
    internal static extern SheetStatus WriteXlsx(SafeSheetHandle sheet, IntPtr scratch,
        byte* path, byte* sheetName, out KhzXlsxReportNative report);
}
