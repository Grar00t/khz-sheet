using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace KHZ.Sheet.Core
{
	/// <summary>Which SIMD kernel was compiled into the native library.</summary>
	public enum SimdKernel
	{
		Scalar = 0,
		Avx2 = 1,
		Neon = 2,
	}

	/// <summary>Mirror of KhzCellKind.</summary>
	public enum CellKind
	{
		Empty = 0,
		Rational = 1,
		Text = 2,
		Bool = 3,
		Error = 4,
		Formula = 5,
	}

	/// <summary>Mirror of KhzCellError.</summary>
	public enum CellErrorCode
	{
		None = 0,
		Null = 1,
		Div0 = 2,
		Value = 3,
		Ref = 4,
		Name = 5,
		Num = 6,
		Na = 7,
	}

	/// <summary>
	/// Mirror of KhzRational. Exact num/den, never a double. Two int64 fields,
	/// blittable, passed by value across the boundary exactly as C does.
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	public struct KhzRationalNative
	{
		public long Num;
		public long Den;

		public bool IsInteger
		{
			get { return Den == 1L; }
		}

		public override string ToString()
		{
			return Den == 1L
				? Num.ToString(System.Globalization.CultureInfo.InvariantCulture)
				: Num.ToString(System.Globalization.CultureInfo.InvariantCulture)
					+ "/"
					+ Den.ToString(System.Globalization.CultureInfo.InvariantCulture);
		}
	}

	/// <summary>Mirror of KhzAbiSizes.</summary>
	[StructLayout(LayoutKind.Sequential)]
	public struct KhzAbiSizes
	{
		public uint Version;
		public uint PointerBytes;
		public uint SizeTBytes;
		public uint ArenaBytes;
		public uint RationalBytes;
		public uint CellBytes;
		public uint GridSlotBytes;
		public uint GridBytes;
		public uint DepGraphBytes;
		public uint SheetBytes;
		public uint LedgerBytes;
		public uint CellProofOffset;
		public uint CellValueOffset;
		public uint Sha256DigestBytes;
	}

	/// <summary>
	/// Mirror of KhzCell, field for field. Read through a pointer into the
	/// native arena: no copy is made and no marshalling runs. Text and Formula
	/// point into that same arena and are valid only while the sheet is alive.
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	public unsafe struct KhzCellNative
	{
		public uint Col;
		public uint Row;
		public uint KindRaw;
		public uint ErrorRaw;
		public KhzRationalNative Value;
		public IntPtr Text;
		public IntPtr Formula;
		public uint TextLen;
		public uint FormulaLen;
		public uint BoolValue;
		public uint Flags;
		public ulong Revision;
		public fixed byte Proof[32];
		public fixed byte Reserved[24];

		public CellKind Kind
		{
			get { return (CellKind)KindRaw; }
		}

		public CellErrorCode ErrorCode
		{
			get { return (CellErrorCode)ErrorRaw; }
		}

		/// <summary>True once the cell has been committed onto the proof chain.</summary>
		public bool IsCommitted
		{
			get { return Revision != 0UL; }
		}
	}

	/// <summary>
	/// Raw entry points. Every one returns an int that is a KhzSheetStatus, a
	/// KhzArenaStatus, or a plain count, and none of them throws: a failure
	/// arrives as a value, which is the whole point of the C contract.
	/// </summary>
	internal static unsafe class KhzNative
	{
		/// <summary>
		/// Base name only. The runtime resolves libkhz_sheet.so, khz_sheet.dll or
		/// libkhz_sheet.dylib. Built by the khz_sheet_shared CMake target, which
		/// must be on the loader path.
		/// </summary>
		internal const string Lib = "khz_sheet";

		[DllImport(Lib, EntryPoint = "khz_abi_sizes", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int AbiSizes(KhzAbiSizes* sizes);

		[DllImport(Lib, EntryPoint = "khz_abi_version", CallingConvention = CallingConvention.Cdecl)]
		internal static extern uint AbiVersion();

		[DllImport(Lib, EntryPoint = "khz_abi_ledger_compiled", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int AbiLedgerCompiled();

		[DllImport(Lib, EntryPoint = "khz_simd_kernel", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int SimdKernelId();

		[DllImport(Lib, EntryPoint = "khz_simd_lanes", CallingConvention = CallingConvention.Cdecl)]
		internal static extern nuint SimdLanes();

		[DllImport(Lib, EntryPoint = "khz_sheet_selftest", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int SheetSelftest();

		[DllImport(Lib, EntryPoint = "khz_sheet_init", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int SheetInit(void* sheet, nuint arenaBytes, nuint cellCapacity);

		[DllImport(Lib, EntryPoint = "khz_sheet_destroy", CallingConvention = CallingConvention.Cdecl)]
		internal static extern void SheetDestroy(void* sheet);

		[DllImport(Lib, EntryPoint = "khz_sheet_set_i64", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int SheetSetI64(void* sheet, uint col, uint row, long value);

		[DllImport(Lib, EntryPoint = "khz_sheet_set_rational", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int SheetSetRational(void* sheet, uint col, uint row, KhzRationalNative value);

		[DllImport(Lib, EntryPoint = "khz_sheet_set_text", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int SheetSetText(void* sheet, uint col, uint row, byte* text, nuint len);

		[DllImport(Lib, EntryPoint = "khz_sheet_set_bool", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int SheetSetBool(void* sheet, uint col, uint row, int value);

		[DllImport(Lib, EntryPoint = "khz_sheet_set_error", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int SheetSetError(void* sheet, uint col, uint row, int error);

		[DllImport(Lib, EntryPoint = "khz_sheet_set_formula", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int SheetSetFormula(void* sheet, uint col, uint row, byte* formula, nuint len);

		[DllImport(Lib, EntryPoint = "khz_sheet_get", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int SheetGet(void* sheet, uint col, uint row, KhzCellNative** cell);

		[DllImport(Lib, EntryPoint = "khz_sheet_sum", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int SheetSum(void* sheet, uint col0, uint row0, uint col1, uint row1,
			KhzRationalNative* result, nuint* counted);

		[DllImport(Lib, EntryPoint = "khz_sheet_avg", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int SheetAvg(void* sheet, uint col0, uint row0, uint col1, uint row1,
			KhzRationalNative* result, nuint* counted);

		[DllImport(Lib, EntryPoint = "khz_sheet_declare_dependency", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int SheetDeclareDependency(void* sheet, uint fromCol, uint fromRow,
			uint toCol, uint toRow);

		[DllImport(Lib, EntryPoint = "khz_sheet_evaluation_order", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int SheetEvaluationOrder(void* sheet, nuint* order, nuint capacity, nuint* count);

		[DllImport(Lib, EntryPoint = "khz_sheet_verify_chain", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int SheetVerifyChain(void* sheet, nuint* failedIndex);

		[DllImport(Lib, EntryPoint = "khz_sheet_proof_hex", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int SheetProofHex(void* sheet, byte* hex);

		[DllImport(Lib, EntryPoint = "khz_sheet_cell_count", CallingConvention = CallingConvention.Cdecl)]
		internal static extern nuint SheetCellCount(void* sheet);

		[DllImport(Lib, EntryPoint = "khz_sheet_commits", CallingConvention = CallingConvention.Cdecl)]
		internal static extern ulong SheetCommits(void* sheet);

		[DllImport(Lib, EntryPoint = "khz_sheet_arena", CallingConvention = CallingConvention.Cdecl)]
		internal static extern void* SheetArena(void* sheet);

		[DllImport(Lib, EntryPoint = "khz_arena_used", CallingConvention = CallingConvention.Cdecl)]
		internal static extern nuint ArenaUsed(void* arena);

		[DllImport(Lib, EntryPoint = "khz_arena_capacity", CallingConvention = CallingConvention.Cdecl)]
		internal static extern nuint ArenaCapacity(void* arena);

		[DllImport(Lib, EntryPoint = "khz_arena_peak", CallingConvention = CallingConvention.Cdecl)]
		internal static extern nuint ArenaPeak(void* arena);

		[DllImport(Lib, EntryPoint = "khz_arena_allocations", CallingConvention = CallingConvention.Cdecl)]
		internal static extern ulong ArenaAllocations(void* arena);

		[DllImport(Lib, EntryPoint = "khz_arena_rejections", CallingConvention = CallingConvention.Cdecl)]
		internal static extern ulong ArenaRejections(void* arena);

		[DllImport(Lib, EntryPoint = "khz_arena_backing", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int ArenaBacking(void* arena);

		[DllImport(Lib, EntryPoint = "khz_grid_count", CallingConvention = CallingConvention.Cdecl)]
		internal static extern nuint GridCount(void* grid);

		[DllImport(Lib, EntryPoint = "khz_grid_capacity", CallingConvention = CallingConvention.Cdecl)]
		internal static extern nuint GridCapacity(void* grid);

		[DllImport(Lib, EntryPoint = "khz_grid_probes", CallingConvention = CallingConvention.Cdecl)]
		internal static extern ulong GridProbes(void* grid);

		[DllImport(Lib, EntryPoint = "khz_grid_rejections", CallingConvention = CallingConvention.Cdecl)]
		internal static extern ulong GridRejections(void* grid);

		[DllImport(Lib, EntryPoint = "khz_ledger_available", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int LedgerAvailable();

		[DllImport(Lib, EntryPoint = "khz_ledger_init", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int LedgerInit(void* ledger, void* arena);

		[DllImport(Lib, EntryPoint = "khz_ledger_open", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int LedgerOpen(void* ledger, byte* path);

		[DllImport(Lib, EntryPoint = "khz_ledger_close", CallingConvention = CallingConvention.Cdecl)]
		internal static extern void LedgerClose(void* ledger);

		[DllImport(Lib, EntryPoint = "khz_ledger_append_cell", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int LedgerAppendCell(void* ledger, KhzCellNative* cell);

		[DllImport(Lib, EntryPoint = "khz_ledger_append_sheet", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int LedgerAppendSheet(void* ledger, void* sheet);

		[DllImport(Lib, EntryPoint = "khz_ledger_verify_chain", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int LedgerVerifyChain(void* ledger, ulong* checkedRows, long* failedId);

		[DllImport(Lib, EntryPoint = "khz_ledger_cell_history_count", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int LedgerCellHistoryCount(void* ledger, uint col, uint row, ulong* count);

		[DllImport(Lib, EntryPoint = "khz_ledger_head_hex", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int LedgerHeadHex(void* ledger, byte* hex);

		[DllImport(Lib, EntryPoint = "khz_ledger_rows", CallingConvention = CallingConvention.Cdecl)]
		internal static extern ulong LedgerRows(void* ledger);
	}

	/// <summary>
	/// Checks that the managed struct mirrors match the native ones before any
	/// pointer is dereferenced. Without this a layout change in C would be read
	/// at wrong offsets and corrupt memory silently, which is far worse than a
	/// refused call.
	/// </summary>
	public static unsafe class KhzAbi
	{
		private static bool _verified;
		private static SheetStatus _result = SheetStatus.ErrState;
		private static string _detail = "not checked";
		private static KhzAbiSizes _sizes;

		/// <summary>
		/// Ok when the native library agrees with these bindings. ErrUnsupported
		/// on a version or layout mismatch. ErrMissing when the library could not
		/// be loaded at all. Never throws for any of those.
		/// </summary>
		public static SheetStatus Verify(out string detail)
		{
			if (_verified)
			{
				detail = _detail;
				return _result;
			}

			_verified = true;

			KhzAbiSizes sizes;

			try
			{
				int rc;

				fixed (KhzAbiSizes* slot = &sizes)
				{
					rc = KhzNative.AbiSizes(slot);
				}

				if ((SheetStatus)rc != SheetStatus.Ok)
				{
					_result = (SheetStatus)rc;
					_detail = "khz_abi_sizes returned " + SheetStatusText.Name(_result);
					detail = _detail;
					return _result;
				}
			}
			catch (DllNotFoundException)
			{
				// Loading a shared library is the one thing the runtime signals by
				// throwing. It is converted here so callers keep a status-only API.
				_result = SheetStatus.ErrMissing;
				_detail = "native library " + KhzNative.Lib + " not found on the loader path";
				detail = _detail;
				return _result;
			}
			catch (EntryPointNotFoundException)
			{
				_result = SheetStatus.ErrUnsupported;
				_detail = "native library is present but predates the Phase 91 ABI";
				detail = _detail;
				return _result;
			}

			if (sizes.Version != 91u)
			{
				_result = SheetStatus.ErrUnsupported;
				_detail = "native ABI version " + sizes.Version.ToString() + ", bindings expect 91";
				detail = _detail;
				return _result;
			}

			if (sizes.PointerBytes != (uint)sizeof(void*))
			{
				_result = SheetStatus.ErrUnsupported;
				_detail = "pointer width mismatch: native " + sizes.PointerBytes.ToString()
					+ ", managed " + sizeof(void*).ToString();
				detail = _detail;
				return _result;
			}

			if (sizes.CellBytes != (uint)sizeof(KhzCellNative))
			{
				_result = SheetStatus.ErrUnsupported;
				_detail = "KhzCell size mismatch: native " + sizes.CellBytes.ToString()
					+ ", managed " + sizeof(KhzCellNative).ToString();
				detail = _detail;
				return _result;
			}

			if (sizes.RationalBytes != (uint)sizeof(KhzRationalNative))
			{
				_result = SheetStatus.ErrUnsupported;
				_detail = "KhzRational size mismatch";
				detail = _detail;
				return _result;
			}

			if (sizes.CellProofOffset != 72u || sizes.CellValueOffset != 16u)
			{
				// Offsets are checked as well as sizes: two different layouts can
				// share a total size while placing fields elsewhere.
				_result = SheetStatus.ErrUnsupported;
				_detail = "KhzCell field offsets differ from the mirrored layout";
				detail = _detail;
				return _result;
			}

			_sizes = sizes;
			_result = SheetStatus.Ok;
			_detail = "ok";
			detail = _detail;
			return _result;
		}

		internal static SheetStatus Sizes(out KhzAbiSizes sizes)
		{
			string ignored;
			SheetStatus status = Verify(out ignored);

			sizes = _sizes;
			return status;
		}

		/// <summary>Which kernel the native library was compiled with.</summary>
		public static SheetStatus TryKernel(out SimdKernel kernel, out nuint lanes)
		{
			string ignored;
			SheetStatus status = Verify(out ignored);

			if (status != SheetStatus.Ok)
			{
				kernel = SimdKernel.Scalar;
				lanes = (nuint)0;
				return status;
			}

			kernel = (SimdKernel)KhzNative.SimdKernelId();
			lanes = KhzNative.SimdLanes();
			return SheetStatus.Ok;
		}

		/// <summary>Runs the native known-answer tests. Zero means all matched.</summary>
		public static SheetStatus TrySelftest(out int failures)
		{
			string ignored;
			SheetStatus status = Verify(out ignored);

			if (status != SheetStatus.Ok)
			{
				failures = -1;
				return status;
			}

			failures = KhzNative.SheetSelftest();
			return SheetStatus.Ok;
		}
	}

	/// <summary>
	/// Owns one native KhzSheet and therefore one arena.
	///
	/// The control block itself is allocated with NativeMemory.AlignedAlloc
	/// because the C layer deliberately offers no allocator for it: the arena
	/// allocates everything the sheet contains, but something has to hold the
	/// arena struct. That block is a fixed-size, 64-byte-aligned allocation made
	/// once per sheet and freed in Dispose. No cell, slot, edge or string is ever
	/// allocated here; all of those come out of the native arena.
	/// </summary>
	public sealed unsafe class NativeSheet : IDisposable
	{
		private void* _sheet;
		private bool _initialised;

		private NativeSheet(void* sheet)
		{
			_sheet = sheet;
			_initialised = true;
		}

		/// <summary>
		/// Creates a sheet with an explicit arena budget and cell ceiling. Neither
		/// can be raised afterwards; exceeding the ceiling returns ErrLimit rather
		/// than growing.
		/// </summary>
		public static SheetStatus TryCreate(nuint arenaBytes, nuint cellCapacity, out NativeSheet? sheet)
		{
			sheet = null;

			KhzAbiSizes sizes;
			SheetStatus status = KhzAbi.Sizes(out sizes);

			if (status != SheetStatus.Ok)
			{
				return status;
			}

			void* block = NativeMemory.AlignedAlloc((nuint)sizes.SheetBytes, (nuint)64);

			if (block == null)
			{
				return SheetStatus.ErrMemory;
			}

			int rc = KhzNative.SheetInit(block, arenaBytes, cellCapacity);

			if ((SheetStatus)rc != SheetStatus.Ok)
			{
				NativeMemory.AlignedFree(block);
				return (SheetStatus)rc;
			}

			sheet = new NativeSheet(block);
			return SheetStatus.Ok;
		}

		internal void* Handle
		{
			get { return _sheet; }
		}

		private SheetStatus Ready()
		{
			return _initialised && _sheet != null ? SheetStatus.Ok : SheetStatus.ErrState;
		}

		public SheetStatus SetInt64(uint col, uint row, long value)
		{
			SheetStatus status = Ready();

			return status != SheetStatus.Ok
				? status
				: (SheetStatus)KhzNative.SheetSetI64(_sheet, col, row, value);
		}

		public SheetStatus SetRational(uint col, uint row, long num, long den)
		{
			SheetStatus status = Ready();

			if (status != SheetStatus.Ok)
			{
				return status;
			}

			KhzRationalNative value;
			value.Num = num;
			value.Den = den;

			return (SheetStatus)KhzNative.SheetSetRational(_sheet, col, row, value);
		}

		/// <summary>
		/// Writes UTF-8 bytes straight from the caller's span. The native side
		/// copies them into its arena, so the span need not outlive the call and
		/// no intermediate managed string is created.
		/// </summary>
		public SheetStatus SetText(uint col, uint row, ReadOnlySpan<byte> utf8)
		{
			SheetStatus status = Ready();

			if (status != SheetStatus.Ok)
			{
				return status;
			}

			fixed (byte* text = utf8)
			{
				return (SheetStatus)KhzNative.SheetSetText(_sheet, col, row, text, (nuint)utf8.Length);
			}
		}

		/// <summary>
		/// Convenience overload. This one does allocate, because a managed string
		/// must be encoded to UTF-8 first; prefer the span overload on a hot path.
		/// </summary>
		public SheetStatus SetText(uint col, uint row, string text)
		{
			if (text == null)
			{
				return SheetStatus.ErrNull;
			}

			return SetText(col, row, Encoding.UTF8.GetBytes(text));
		}

		public SheetStatus SetBool(uint col, uint row, bool value)
		{
			SheetStatus status = Ready();

			return status != SheetStatus.Ok
				? status
				: (SheetStatus)KhzNative.SheetSetBool(_sheet, col, row, value ? 1 : 0);
		}

		public SheetStatus SetError(uint col, uint row, CellErrorCode error)
		{
			SheetStatus status = Ready();

			return status != SheetStatus.Ok
				? status
				: (SheetStatus)KhzNative.SheetSetError(_sheet, col, row, (int)error);
		}

		public SheetStatus SetFormula(uint col, uint row, ReadOnlySpan<byte> utf8)
		{
			SheetStatus status = Ready();

			if (status != SheetStatus.Ok)
			{
				return status;
			}

			fixed (byte* formula = utf8)
			{
				return (SheetStatus)KhzNative.SheetSetFormula(_sheet, col, row, formula, (nuint)utf8.Length);
			}
		}

		/// <summary>
		/// Hands back a pointer into the arena. Nothing is copied and nothing is
		/// marshalled. The pointer is valid until this sheet is disposed; storing
		/// it past that point is a use-after-free, which is why the accessor is
		/// pointer-shaped rather than pretending to be a managed object.
		/// </summary>
		public SheetStatus TryGetCellPointer(uint col, uint row, out KhzCellNative* cell)
		{
			cell = null;

			SheetStatus status = Ready();

			if (status != SheetStatus.Ok)
			{
				return status;
			}

			KhzCellNative* found = null;
			int rc = KhzNative.SheetGet(_sheet, col, row, &found);

			if ((SheetStatus)rc != SheetStatus.Ok)
			{
				return (SheetStatus)rc;
			}

			cell = found;
			return SheetStatus.Ok;
		}

		/// <summary>Copying read for callers that do not want to touch pointers.</summary>
		public SheetStatus TryGetCell(uint col, uint row, out KhzCellNative cell)
		{
			KhzCellNative* pointer;
			SheetStatus status = TryGetCellPointer(col, row, out pointer);

			if (status != SheetStatus.Ok)
			{
				cell = default(KhzCellNative);
				return status;
			}

			cell = *pointer;
			return SheetStatus.Ok;
		}

		/// <summary>
		/// The cell's text as a span over arena memory. Zero copy: the bytes are
		/// the native ones. Not NUL terminated, and not valid after Dispose.
		/// </summary>
		public SheetStatus TryGetTextBytes(uint col, uint row, out ReadOnlySpan<byte> utf8)
		{
			utf8 = default(ReadOnlySpan<byte>);

			KhzCellNative* cell;
			SheetStatus status = TryGetCellPointer(col, row, out cell);

			if (status != SheetStatus.Ok)
			{
				return status;
			}

			if (cell->Kind != CellKind.Text)
			{
				return SheetStatus.ErrType;
			}

			if (cell->Text == IntPtr.Zero || cell->TextLen == 0u)
			{
				return SheetStatus.ErrMissing;
			}

			utf8 = new ReadOnlySpan<byte>((void*)cell->Text, (int)cell->TextLen);
			return SheetStatus.Ok;
		}

		public SheetStatus TrySum(uint col0, uint row0, uint col1, uint row1,
			out KhzRationalNative result, out nuint counted)
		{
			result = default(KhzRationalNative);
			counted = (nuint)0;

			SheetStatus status = Ready();

			if (status != SheetStatus.Ok)
			{
				return status;
			}

			KhzRationalNative value;
			nuint hits;
			int rc = KhzNative.SheetSum(_sheet, col0, row0, col1, row1, &value, &hits);

			if ((SheetStatus)rc != SheetStatus.Ok)
			{
				return (SheetStatus)rc;
			}

			result = value;
			counted = hits;
			return SheetStatus.Ok;
		}

		/// <summary>
		/// Exact mean. AVERAGE(1,2) returns 3/2, not 1.5: the result stays a
		/// rational so no rounding is introduced at this boundary. An empty range
		/// returns ErrDivZero, matching the spreadsheet rule rather than answering
		/// zero.
		/// </summary>
		public SheetStatus TryAverage(uint col0, uint row0, uint col1, uint row1,
			out KhzRationalNative result, out nuint counted)
		{
			result = default(KhzRationalNative);
			counted = (nuint)0;

			SheetStatus status = Ready();

			if (status != SheetStatus.Ok)
			{
				return status;
			}

			KhzRationalNative value;
			nuint hits;
			int rc = KhzNative.SheetAvg(_sheet, col0, row0, col1, row1, &value, &hits);

			if ((SheetStatus)rc != SheetStatus.Ok)
			{
				return (SheetStatus)rc;
			}

			result = value;
			counted = hits;
			return SheetStatus.Ok;
		}

		public SheetStatus DeclareDependency(uint fromCol, uint fromRow, uint toCol, uint toRow)
		{
			SheetStatus status = Ready();

			return status != SheetStatus.Ok
				? status
				: (SheetStatus)KhzNative.SheetDeclareDependency(_sheet, fromCol, fromRow, toCol, toRow);
		}

		/// <summary>
		/// Topological evaluation order. ErrCycle on a circular reference: the
		/// cycle is reported, never broken at an arbitrary edge to force a result.
		/// </summary>
		public SheetStatus TryEvaluationOrder(Span<nuint> order, out nuint count)
		{
			count = (nuint)0;

			SheetStatus status = Ready();

			if (status != SheetStatus.Ok)
			{
				return status;
			}

			nuint produced;

			fixed (nuint* slots = order)
			{
				int rc = KhzNative.SheetEvaluationOrder(_sheet, slots, (nuint)order.Length, &produced);

				if ((SheetStatus)rc != SheetStatus.Ok)
				{
					return (SheetStatus)rc;
				}
			}

			count = produced;
			return SheetStatus.Ok;
		}

		public SheetStatus VerifyChain(out nuint failedIndex)
		{
			failedIndex = (nuint)0;

			SheetStatus status = Ready();

			if (status != SheetStatus.Ok)
			{
				return status;
			}

			nuint index = (nuint)0;
			int rc = KhzNative.SheetVerifyChain(_sheet, &index);

			failedIndex = index;
			return (SheetStatus)rc;
		}

		/// <summary>Chain head as 64 lowercase hex characters.</summary>
		public SheetStatus TryProofHex(out string hex)
		{
			hex = string.Empty;

			SheetStatus status = Ready();

			if (status != SheetStatus.Ok)
			{
				return status;
			}

			byte* buffer = stackalloc byte[65];
			int rc = KhzNative.SheetProofHex(_sheet, buffer);

			if ((SheetStatus)rc != SheetStatus.Ok)
			{
				return (SheetStatus)rc;
			}

			hex = Encoding.ASCII.GetString(buffer, 64);
			return SheetStatus.Ok;
		}

		public nuint CellCount
		{
			get { return _sheet == null ? (nuint)0 : KhzNative.SheetCellCount(_sheet); }
		}

		public ulong Commits
		{
			get { return _sheet == null ? 0UL : KhzNative.SheetCommits(_sheet); }
		}

		/// <summary>
		/// Arena counters, read straight from the atomics. Rejections above zero
		/// means an allocation was refused: the arena never silently grew, so this
		/// number is the honest record of what did not fit.
		/// </summary>
		public SheetStatus TryArenaStats(out nuint used, out nuint capacity, out nuint peak,
			out ulong allocations, out ulong rejections)
		{
			used = (nuint)0;
			capacity = (nuint)0;
			peak = (nuint)0;
			allocations = 0UL;
			rejections = 0UL;

			SheetStatus status = Ready();

			if (status != SheetStatus.Ok)
			{
				return status;
			}

			void* arena = KhzNative.SheetArena(_sheet);

			if (arena == null)
			{
				return SheetStatus.ErrState;
			}

			used = KhzNative.ArenaUsed(arena);
			capacity = KhzNative.ArenaCapacity(arena);
			peak = KhzNative.ArenaPeak(arena);
			allocations = KhzNative.ArenaAllocations(arena);
			rejections = KhzNative.ArenaRejections(arena);

			return SheetStatus.Ok;
		}

		public void Dispose()
		{
			if (_sheet == null)
			{
				return;
			}

			if (_initialised)
			{
				// Returns the whole arena reservation in one call. Every cell,
				// slot, edge and string dies with it.
				KhzNative.SheetDestroy(_sheet);
				_initialised = false;
			}

			NativeMemory.AlignedFree(_sheet);
			_sheet = null;
		}
	}

	/// <summary>
	/// Adapter over the native SQLite ledger. Same allocation note as
	/// NativeSheet: the control block is one aligned native allocation, and the
	/// sqlite3 handles inside it are the documented exception to the no-heap
	/// rule. The ledger borrows the sheet's arena and must not outlive it.
	/// </summary>
	public sealed unsafe class NativeLedger : IDisposable
	{
		private void* _ledger;
		private bool _open;

		private NativeLedger(void* ledger)
		{
			_ledger = ledger;
		}

		/// <summary>
		/// ErrUnsupported when the native library was built without sqlite3. That
		/// is reported rather than silently accepting commits that would never be
		/// written anywhere.
		/// </summary>
		public static SheetStatus TryOpen(NativeSheet sheet, string path, out NativeLedger? ledger)
		{
			ledger = null;

			if (sheet == null || path == null)
			{
				return SheetStatus.ErrNull;
			}

			KhzAbiSizes sizes;
			SheetStatus status = KhzAbi.Sizes(out sizes);

			if (status != SheetStatus.Ok)
			{
				return status;
			}

			if (KhzNative.LedgerAvailable() == 0)
			{
				return SheetStatus.ErrUnsupported;
			}

			void* arena = KhzNative.SheetArena(sheet.Handle);

			if (arena == null)
			{
				return SheetStatus.ErrState;
			}

			void* block = NativeMemory.AlignedAlloc((nuint)sizes.LedgerBytes, (nuint)64);

			if (block == null)
			{
				return SheetStatus.ErrMemory;
			}

			int rc = KhzNative.LedgerInit(block, arena);

			if ((SheetStatus)rc != SheetStatus.Ok)
			{
				NativeMemory.AlignedFree(block);
				return (SheetStatus)rc;
			}

			byte[] utf8 = Encoding.UTF8.GetBytes(path + "\0");

			fixed (byte* pathBytes = utf8)
			{
				rc = KhzNative.LedgerOpen(block, pathBytes);
			}

			if ((SheetStatus)rc != SheetStatus.Ok)
			{
				NativeMemory.AlignedFree(block);
				return (SheetStatus)rc;
			}

			NativeLedger created = new NativeLedger(block);
			created._open = true;
			ledger = created;

			return SheetStatus.Ok;
		}

		private SheetStatus Ready()
		{
			return _open && _ledger != null ? SheetStatus.Ok : SheetStatus.ErrState;
		}

		/// <summary>
		/// Records one committed cell. ErrFormat when the cell's proof does not
		/// chain onto the ledger head: the ledger only records links it can derive
		/// itself.
		/// </summary>
		public SheetStatus AppendCell(KhzCellNative* cell)
		{
			SheetStatus status = Ready();

			if (status != SheetStatus.Ok)
			{
				return status;
			}

			if (cell == null)
			{
				return SheetStatus.ErrNull;
			}

			return (SheetStatus)KhzNative.LedgerAppendCell(_ledger, cell);
		}

		public SheetStatus AppendSheet(NativeSheet sheet)
		{
			SheetStatus status = Ready();

			if (status != SheetStatus.Ok)
			{
				return status;
			}

			if (sheet == null)
			{
				return SheetStatus.ErrNull;
			}

			return (SheetStatus)KhzNative.LedgerAppendSheet(_ledger, sheet.Handle);
		}

		/// <summary>
		/// Replays the recorded chain. Proves linkage and ordering only: the
		/// ledger stores hashes, not payloads, so it cannot prove that a hash
		/// corresponds to a particular cell value.
		/// </summary>
		public SheetStatus VerifyChain(out ulong checkedRows, out long failedId)
		{
			checkedRows = 0UL;
			failedId = -1L;

			SheetStatus status = Ready();

			if (status != SheetStatus.Ok)
			{
				return status;
			}

			ulong seen = 0UL;
			long broken = -1L;
			int rc = KhzNative.LedgerVerifyChain(_ledger, &seen, &broken);

			checkedRows = seen;
			failedId = broken;
			return (SheetStatus)rc;
		}

		/// <summary>
		/// How many times one coordinate has been committed. The grid cannot
		/// answer this after a rewrite, which is the reason the ledger exists.
		/// </summary>
		public SheetStatus TryCellHistoryCount(uint col, uint row, out ulong count)
		{
			count = 0UL;

			SheetStatus status = Ready();

			if (status != SheetStatus.Ok)
			{
				return status;
			}

			ulong value = 0UL;
			int rc = KhzNative.LedgerCellHistoryCount(_ledger, col, row, &value);

			if ((SheetStatus)rc != SheetStatus.Ok)
			{
				return (SheetStatus)rc;
			}

			count = value;
			return SheetStatus.Ok;
		}

		public SheetStatus TryHeadHex(out string hex)
		{
			hex = string.Empty;

			SheetStatus status = Ready();

			if (status != SheetStatus.Ok)
			{
				return status;
			}

			byte* buffer = stackalloc byte[65];
			int rc = KhzNative.LedgerHeadHex(_ledger, buffer);

			if ((SheetStatus)rc != SheetStatus.Ok)
			{
				return (SheetStatus)rc;
			}

			hex = Encoding.ASCII.GetString(buffer, 64);
			return SheetStatus.Ok;
		}

		public ulong Rows
		{
			get { return _ledger == null ? 0UL : KhzNative.LedgerRows(_ledger); }
		}

		public void Dispose()
		{
			if (_ledger == null)
			{
				return;
			}

			if (_open)
			{
				KhzNative.LedgerClose(_ledger);
				_open = false;
			}

			NativeMemory.AlignedFree(_ledger);
			_ledger = null;
		}
	}
}
