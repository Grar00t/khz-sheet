using System;

namespace KHZ.Sheet.Core
{
	/// <summary>
	/// Every fallible operation returns one of these. No exception is used for a
	/// condition the caller can be expected to hit, and no operation guesses a
	/// value in order to keep going.
	///
	/// These values are the same integers as KhzSheetStatus in
	/// include/khz_status.h. The C layer returns them directly across the
	/// P/Invoke boundary, so the two enums must stay numerically identical:
	/// changing one without the other would turn a known failure into an
	/// unnamed integer.
	/// </summary>
	public enum SheetStatus
	{
		Ok = 0,

		/// <summary>A required argument was null.</summary>
		ErrNull = -1,

		/// <summary>A value was outside the legal range for its kind.</summary>
		ErrRange = -2,

		/// <summary>Input was not in the expected shape.</summary>
		ErrFormat = -3,

		/// <summary>A declared ceiling was exceeded.</summary>
		ErrLimit = -4,

		/// <summary>The referenced thing does not exist.</summary>
		ErrMissing = -5,

		/// <summary>The operation is legal but not implemented, and is not faked.</summary>
		ErrUnsupported = -6,

		/// <summary>The exact result is not representable. Nothing is rounded.</summary>
		ErrOverflow = -7,

		/// <summary>Division by zero. Never an infinity and never a trap.</summary>
		ErrDivZero = -8,

		/// <summary>The object was not in a state where this call is meaningful.</summary>
		ErrState = -9,

		/// <summary>The arena is exhausted. There is no fallback to the heap.</summary>
		ErrMemory = -10,

		/// <summary>A circular reference. Reported, never broken arbitrarily.</summary>
		ErrCycle = -11,

		/// <summary>A cell held a kind this operation cannot consume.</summary>
		ErrType = -12,
	}

	/// <summary>Names for status codes, so logs never print a bare integer.</summary>
	public static class SheetStatusText
	{
		public static string Name(SheetStatus status)
		{
			switch (status)
			{
				case SheetStatus.Ok:
					return "OK";
				case SheetStatus.ErrNull:
					return "ERR_NULL";
				case SheetStatus.ErrRange:
					return "ERR_RANGE";
				case SheetStatus.ErrFormat:
					return "ERR_FORMAT";
				case SheetStatus.ErrLimit:
					return "ERR_LIMIT";
				case SheetStatus.ErrMissing:
					return "ERR_MISSING";
				case SheetStatus.ErrUnsupported:
					return "ERR_UNSUPPORTED";
				case SheetStatus.ErrOverflow:
					return "ERR_OVERFLOW";
				case SheetStatus.ErrDivZero:
					return "ERR_DIVZERO";
				case SheetStatus.ErrState:
					return "ERR_STATE";
				case SheetStatus.ErrMemory:
					return "ERR_MEMORY";
				case SheetStatus.ErrCycle:
					return "ERR_CYCLE";
				case SheetStatus.ErrType:
					return "ERR_TYPE";
				default:
					return "ERR_UNKNOWN";
			}
		}
	}
}
