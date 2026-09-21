using System;
using System.Runtime.InteropServices;

namespace KHZ.Sheet.Core
{
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
	/// Entry points added after Phase 91, declared here rather than in
	/// KhzNative.cs.
	///
	/// Not a stylistic choice: KhzNative.cs is thirty kilobytes, complete-file
	/// pushes are the only commit form this repository uses, and a push that
	/// size has already truncated a file here once. Declaring the two queries
	/// this gate needs beside the gate itself costs nothing - a DllImport is a
	/// declaration, so several classes may declare the same native function
	/// without conflict.
	/// </summary>
	internal static class KhzAbiNative
	{
		internal const string Lib = "khz_sheet";

		[DllImport(Lib, EntryPoint = "khz_abi_dep_edge_bytes",
			CallingConvention = CallingConvention.Cdecl)]
		internal static extern nuint DepEdgeBytes();

		[DllImport(Lib, EntryPoint = "khz_abi_dep_range_edge_bytes",
			CallingConvention = CallingConvention.Cdecl)]
		internal static extern nuint DepRangeEdgeBytes();

		[DllImport(Lib, EntryPoint = "khz_abi_range_edges_compiled",
			CallingConvention = CallingConvention.Cdecl)]
		internal static extern int RangeEdgesCompiled();
	}

	/// <summary>
	/// Checks that the managed struct mirrors match the native ones before any
	/// pointer is dereferenced. Without this a layout change in C would be read
	/// at wrong offsets and corrupt memory silently, which is far worse than a
	/// refused call.
	///
	/// Split out of KhzNative.cs in Phase 95. The gate is the one part of the
	/// bindings that has to change every time a C struct changes, and it does
	/// not belong in the middle of thirty kilobytes of entry-point
	/// declarations where editing it means rewriting all of them.
	/// </summary>
	public static unsafe class KhzAbi
	{
		/// <summary>
		/// Oldest native ABI these bindings can work against.
		///
		/// 91 is usable but reduced: it predates khz_abi_formula_bytes(), so the
		/// managed formula lowerer refuses to run against it and reports
		/// ErrUnsupported rather than falling back to a guessed struct size.
		/// </summary>
		public const uint MinimumVersion = 91u;

		/// <summary>
		/// Newest native ABI these bindings were written against.
		///
		/// A library reporting more than this is refused. That direction is the
		/// dangerous one: a newer library may have moved a field these bindings
		/// still mirror by hand, and accepting it would mean reading a KhzCell at
		/// offsets that no longer describe it. Refusing to load is recoverable;
		/// reading a struct at stale offsets is not.
		///
		/// 95 introduced retroactive dependency regions. ABI 96 extends the
		/// public native KhzCommitEntry with the predecessor proof head so a
		/// retained superseded transition can be authenticated. Managed code does
		/// not mirror KhzCommitEntry, but widening this gate keeps the native
		/// public-layout version discipline intact.
		/// </summary>
		public const uint CurrentVersion = 96u;

		/// <summary>First ABI revision that resolves ranges as regions.</summary>
		public const uint RangeEdgeVersion = 95u;

		/// <summary>
		/// Byte offsets of the two KhzCell fields this layer mirrors by hand.
		/// Checked as well as sizes because two different layouts can share a
		/// total size while placing fields elsewhere.
		/// </summary>
		private const uint ExpectedCellProofOffset = 72u;
		private const uint ExpectedCellValueOffset = 16u;

		private static bool _verified;
		private static SheetStatus _result = SheetStatus.ErrState;
		private static string _detail = "not checked";
		private static KhzAbiSizes _sizes;
		private static nuint _rangeEdgeBytes;

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

			KhzAbiSizes sizes = default;

			try
			{
				int rc = KhzNative.AbiSizes(&sizes);

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

			if (sizes.Version < MinimumVersion)
			{
				_result = SheetStatus.ErrUnsupported;
				_detail = "native ABI version " + sizes.Version.ToString()
					+ " is older than the minimum these bindings support ("
					+ MinimumVersion.ToString() + ")";
				detail = _detail;
				return _result;
			}

			if (sizes.Version > CurrentVersion)
			{
				_result = SheetStatus.ErrUnsupported;
				_detail = "native ABI version " + sizes.Version.ToString()
					+ " is newer than these bindings were written against ("
					+ CurrentVersion.ToString() + "); rebuild the managed layer";
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

			if (sizes.CellProofOffset != ExpectedCellProofOffset
				|| sizes.CellValueOffset != ExpectedCellValueOffset)
			{
				_result = SheetStatus.ErrUnsupported;
				_detail = "KhzCell field offsets differ from the mirrored layout: proof "
					+ sizes.CellProofOffset.ToString() + ", value "
					+ sizes.CellValueOffset.ToString();
				detail = _detail;
				return _result;
			}

			SheetStatus rangeCheck = VerifyRangeEdgeLayout(sizes, out string rangeDetail);

			if (rangeCheck != SheetStatus.Ok)
			{
				_result = rangeCheck;
				_detail = rangeDetail;
				detail = _detail;
				return _result;
			}

			_sizes = sizes;
			_result = SheetStatus.Ok;
			_detail = "ok, native ABI " + sizes.Version.ToString() + rangeDetail;
			detail = _detail;
			return _result;
		}

		private static SheetStatus VerifyRangeEdgeLayout(KhzAbiSizes sizes, out string detail)
		{
			if (sizes.Version < RangeEdgeVersion)
			{
				detail = ", ranges frozen (pre-95 library)";
				return SheetStatus.Ok;
			}

			nuint actual;

			try
			{
				actual = KhzAbiNative.DepRangeEdgeBytes();
			}
			catch (EntryPointNotFoundException)
			{
				detail = "native ABI " + sizes.Version.ToString()
					+ " does not export khz_abi_dep_range_edge_bytes";
				return SheetStatus.ErrUnsupported;
			}

			nuint expected = (nuint)16 + (nuint)(2u * sizes.SizeTBytes)
				+ (nuint)sizes.PointerBytes;
			nuint tolerance = expected + (nuint)sizes.PointerBytes;

			if (actual < expected || actual > tolerance)
			{
				detail = "KhzDepRangeEdge size mismatch: native " + actual.ToString()
					+ ", expected about " + expected.ToString()
					+ " for a " + sizes.PointerBytes.ToString() + "-byte pointer target";
				return SheetStatus.ErrUnsupported;
			}

			_rangeEdgeBytes = actual;
			detail = ", retroactive ranges";
			return SheetStatus.Ok;
		}

		public static nuint RangeEdgeBytes
		{
			get
			{
				string ignored;
				return Verify(out ignored) == SheetStatus.Ok ? _rangeEdgeBytes : (nuint)0;
			}
		}

		public static bool RangeEdgesAvailable
		{
			get
			{
				string ignored;

				if (Verify(out ignored) != SheetStatus.Ok)
				{
					return false;
				}
				if (_sizes.Version < RangeEdgeVersion)
				{
					return false;
				}

				try
				{
					return KhzAbiNative.RangeEdgesCompiled() != 0;
				}
				catch (EntryPointNotFoundException)
				{
					return false;
				}
			}
		}

		internal static SheetStatus Sizes(out KhzAbiSizes sizes)
		{
			string ignored;
			SheetStatus status = Verify(out ignored);

			sizes = _sizes;
			return status;
		}

		public static uint NativeVersion
		{
			get
			{
				string ignored;
				return Verify(out ignored) == SheetStatus.Ok ? _sizes.Version : 0u;
			}
		}

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
}
