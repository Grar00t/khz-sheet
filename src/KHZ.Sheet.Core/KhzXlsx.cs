using System;
using System.Runtime.InteropServices;

namespace KHZ.Sheet.Core
{
    /// <summary>
    /// Native layout of KhzXlsxReport. Field order and widths mirror
    /// include/khz_xlsx.h exactly; six uint64 members with no padding on any
    /// supported target.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct KhzXlsxReportNative
    {
        public ulong CellsWritten;
        public ulong SharedStrings;
        public ulong FormulaCells;
        public ulong LossyCells;
        public ulong BytesWritten;
        public ulong ArenaBytesUsed;
    }

    /// <summary>
    /// Result of an export, in managed terms.
    /// </summary>
    public readonly struct XlsxReport
    {
        public XlsxReport(ulong cellsWritten, ulong sharedStrings, ulong formulaCells,
                          ulong lossyCells, ulong bytesWritten, ulong arenaBytesUsed)
        {
            CellsWritten = cellsWritten;
            SharedStrings = sharedStrings;
            FormulaCells = formulaCells;
            LossyCells = lossyCells;
            BytesWritten = bytesWritten;
            ArenaBytesUsed = arenaBytesUsed;
        }

        public ulong CellsWritten { get; }

        public ulong SharedStrings { get; }

        public ulong FormulaCells { get; }

        /// <summary>
        /// Cells whose exact rational value could not be represented in the
        /// file. xlsx stores numbers as IEEE-754 doubles, so 1/3 becomes the
        /// nearest double. This is the format's limit, not the engine's: the
        /// sheet and the ledger still hold the exact value.
        /// Zero means the file carries every value exactly.
        /// </summary>
        public ulong LossyCells { get; }

        public ulong BytesWritten { get; }

        public ulong ArenaBytesUsed { get; }

        /// <summary>True when nothing was rounded on the way out.</summary>
        public bool IsExact => LossyCells == 0UL;
    }

    /// <summary>
    /// Imports added in Phase 92.
    ///
    /// These live in their own class rather than in KhzNative because
    /// KhzNative is a large non-partial file: re-emitting it wholesale to add
    /// three entry points risks corrupting working bindings for no benefit.
    /// The runtime resolves both classes against the same shared library, so
    /// the split costs nothing at load time.
    /// </summary>
    internal static unsafe class KhzNativeXlsx
    {
        internal const string Lib = "khz_sheet";

        [DllImport(Lib, EntryPoint = "khz_xlsx_write", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int XlsxWrite(void* sheet, void* scratchArena,
                                             [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
                                             [MarshalAs(UnmanagedType.LPUTF8Str)] string? sheetName,
                                             KhzXlsxReportNative* report);

        [DllImport(Lib, EntryPoint = "khz_abi_grid_offset", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nuint GridOffset();

        [DllImport(Lib, EntryPoint = "khz_abi_dep_graph_offset", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nuint DepGraphOffset();

        [DllImport(Lib, EntryPoint = "khz_abi_arena_offset", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nuint ArenaOffset();

        [DllImport(Lib, EntryPoint = "khz_abi_xlsx_compiled", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int XlsxCompiled();
    }

    /// <summary>
    /// Recovers interior pointers into a native KhzSheet.
    ///
    /// The grid, dependency graph and arena are by-value members of KhzSheet,
    /// not separately allocated objects, so there is no accessor to export and
    /// nothing to keep alive independently. Asking the native side for the
    /// member offset once and doing the arithmetic here is the honest way to
    /// reach them: the offset comes from offsetof in the same compiler that
    /// built the struct, so it cannot drift from the real layout the way a
    /// hand-written managed mirror can.
    ///
    /// This is what revives the four khz_grid_* imports in KhzNative that had
    /// no way to obtain a grid pointer.
    /// </summary>
    public static unsafe class KhzSheetLayout
    {
        private static nuint _gridOffset;
        private static nuint _depGraphOffset;
        private static nuint _arenaOffset;
        private static bool _resolved;
        private static readonly object Gate = new object();

        private static void Resolve()
        {
            if (_resolved)
            {
                return;
            }

            lock (Gate)
            {
                if (_resolved)
                {
                    return;
                }

                _gridOffset = KhzNativeXlsx.GridOffset();
                _depGraphOffset = KhzNativeXlsx.DepGraphOffset();
                _arenaOffset = KhzNativeXlsx.ArenaOffset();
                _resolved = true;
            }
        }

        /// <summary>Interior pointer to the sheet's grid, or null for a null sheet.</summary>
        public static void* Grid(void* sheet)
        {
            if (sheet == null)
            {
                return null;
            }

            Resolve();
            return (byte*)sheet + _gridOffset;
        }

        /// <summary>Interior pointer to the sheet's dependency graph.</summary>
        public static void* DepGraph(void* sheet)
        {
            if (sheet == null)
            {
                return null;
            }

            Resolve();
            return (byte*)sheet + _depGraphOffset;
        }

        /// <summary>Interior pointer to the sheet's arena.</summary>
        public static void* Arena(void* sheet)
        {
            if (sheet == null)
            {
                return null;
            }

            Resolve();
            return (byte*)sheet + _arenaOffset;
        }

        /// <summary>True when the native library was built with the xlsx writer.</summary>
        public static bool XlsxAvailable => KhzNativeXlsx.XlsxCompiled() != 0;
    }

    /// <summary>
    /// Managed entry point for xlsx export. Returns SheetStatus; nothing here
    /// throws for an expected outcome such as a bad path or a full arena.
    /// </summary>
    public static unsafe class KhzXlsx
    {
        /// <summary>
        /// Writes the sheet to path. The sheet's own arena is used for scratch
        /// unless a separate one is supplied; the native writer takes a mark
        /// and releases it, so a successful write consumes no lasting memory.
        /// </summary>
        public static SheetStatus Write(void* sheet, string path, string? sheetName,
                                        out XlsxReport report, void* scratchArena = null)
        {
            report = default;

            if (sheet == null)
            {
                return SheetStatus.ErrNull;
            }

            if (string.IsNullOrEmpty(path))
            {
                return SheetStatus.ErrRange;
            }

            void* arena = scratchArena != null ? scratchArena : KhzSheetLayout.Arena(sheet);

            if (arena == null)
            {
                return SheetStatus.ErrNull;
            }

            KhzXlsxReportNative native = default;
            int status = KhzNativeXlsx.XlsxWrite(sheet, arena, path, sheetName, &native);

            if (status == (int)SheetStatus.Ok)
            {
                report = new XlsxReport(native.CellsWritten, native.SharedStrings,
                                        native.FormulaCells, native.LossyCells,
                                        native.BytesWritten, native.ArenaBytesUsed);
            }

            return (SheetStatus)status;
        }
    }
}
