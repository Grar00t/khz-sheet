using System;

namespace KHZ.Sheet.Core
{
	/// <summary>
	/// Every fallible operation returns one of these. No exception is used for a
	/// condition the caller can be expected to hit, and no operation guesses a
	/// value in order to keep going.
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
				default:
					return "ERR_UNKNOWN";
			}
		}
	}
}
