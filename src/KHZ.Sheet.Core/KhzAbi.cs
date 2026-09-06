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
		/// </summary>
		public const uint CurrentVersion = 94u;

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

			/* A range, not an equality.

			   Phase 94 inserted a commit log into KhzSheet and bumped the version
			   to 94, which an equality check against 91 turned into a total
			   refusal to load - the C layer and its own bindings disagreeing over
			   a change that affected neither of the structs this file mirrors.

			   The version is a coarse signal. What actually protects memory here
			   are the size and offset checks below, and the sheet-internal offsets
			   resolved at runtime by KhzSheetLayout. So the version gate is a
			   window of known-good revisions and the structural checks are load
			   bearing. */
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

			_sizes = sizes;
			_result = SheetStatus.Ok;
			_detail = "ok, native ABI " + sizes.Version.ToString();
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

		/// <summary>The native ABI revision, or zero when verification failed.</summary>
		public static uint NativeVersion
		{
			get
			{
				string ignored;
				return Verify(out ignored) == SheetStatus.Ok ? _sizes.Version : 0u;
			}
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
}
