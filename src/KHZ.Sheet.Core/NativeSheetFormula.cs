using System;

namespace KHZ.Sheet.Core
{
	/// <summary>
	/// Bridges NativeSheet to the formula entry points in KhzFormula.
	///
	/// Those entry points take raw pointers, because the C contract does and
	/// because nothing is marshalled across the boundary. The pointers they need
	/// belong to NativeSheet and are internal on purpose, so before this file
	/// existed the formula API was public but unreachable: a caller outside this
	/// assembly had no way to obtain the sheet handle or its arena.
	///
	/// These methods close that gap without widening the handle. The pointer
	/// stays internal, so it cannot be stored by a caller and used after
	/// Dispose.
	///
	/// Every method returns SheetStatus. Nothing here throws, including for a
	/// formula that fails to parse - that is a returned status and a reported
	/// offset, not an exception.
	/// </summary>
	public static unsafe class NativeSheetFormulaExtensions
	{
		/// <summary>
		/// Parses and installs a formula from its text, using the C parser.
		///
		/// This is the preferred path. The text is the one representation both
		/// layers agree on exactly, and it never builds a managed tree, so no
		/// literal passes through a double on the way in.
		///
		/// On failure, failure carries the byte offset the C parser stopped at
		/// and what it expected there.
		/// </summary>
		public static SheetStatus TrySetFormula(this NativeSheet sheet, uint col, uint row,
		                                        string source, out FormulaParseFailure failure)
		{
			failure = new FormulaParseFailure(0UL, string.Empty);

			if (sheet == null)
			{
				return SheetStatus.ErrNull;
			}
			if (sheet.Handle == null)
			{
				return SheetStatus.ErrState;
			}

			return KhzFormula.SetFormula((IntPtr)sheet.Handle, col, row, source, out failure);
		}

		/// <summary>
		/// Recalculates the dirty formula cells and reports how many were
		/// evaluated. ErrCycle means a circular reference was found and nothing
		/// was written.
		/// </summary>
		public static SheetStatus TryRecalculate(this NativeSheet sheet, out ulong evaluated)
		{
			evaluated = 0UL;

			if (sheet == null)
			{
				return SheetStatus.ErrNull;
			}
			if (sheet.Handle == null)
			{
				return SheetStatus.ErrState;
			}

			return KhzFormula.Recalculate((IntPtr)sheet.Handle, out evaluated);
		}

		/// <summary>
		/// Lowers an already-parsed managed tree and evaluates it without storing
		/// the result. Used when the tree came from somewhere other than a cell,
		/// such as a formula read out of a workbook.
		///
		/// col and row are the evaluating cell's position: a relative reference
		/// in the tree resolves against them.
		/// </summary>
		public static SheetStatus TryEvaluate(this NativeSheet sheet, uint col, uint row,
		                                      FormulaNode node, out KhzFormulaResult result)
		{
			result = default;

			IntPtr arena;
			SheetStatus status = ResolveArena(sheet, out arena);

			if (status != SheetStatus.Ok)
			{
				return status;
			}

			return KhzFormula.Evaluate((IntPtr)sheet.Handle, arena, col, row, node, out result);
		}

		/// <summary>
		/// Lowers a managed tree and records the dependency edges it implies, so
		/// a later recalculation visits the cells in topological order.
		/// </summary>
		public static SheetStatus TryDeclareDependencies(this NativeSheet sheet, uint col, uint row,
		                                                 FormulaNode node, out ulong declared)
		{
			declared = 0UL;

			IntPtr arena;
			SheetStatus status = ResolveArena(sheet, out arena);

			if (status != SheetStatus.Ok)
			{
				return status;
			}

			return KhzFormula.DeclareDependencies((IntPtr)sheet.Handle, arena, col, row, node,
			                                      out declared);
		}

		/// <summary>
		/// Resolves the sheet's arena for the scratch IR.
		///
		/// Asked of the sheet on every call rather than cached. The arena belongs
		/// to the sheet, and a managed copy of that pointer would be a second
		/// source of truth for something the sheet is free to change.
		/// </summary>
		private static SheetStatus ResolveArena(NativeSheet sheet, out IntPtr arena)
		{
			arena = IntPtr.Zero;

			if (sheet == null)
			{
				return SheetStatus.ErrNull;
			}
			if (sheet.Handle == null)
			{
				return SheetStatus.ErrState;
			}

			void* native = KhzNative.SheetArena(sheet.Handle);

			if (native == null)
			{
				return SheetStatus.ErrState;
			}

			arena = (IntPtr)native;
			return SheetStatus.Ok;
		}
	}
}
