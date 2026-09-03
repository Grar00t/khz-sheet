using System;
using System.Globalization;

namespace KHZ.Sheet.Core
{
	/// <summary>
	/// A zero-based cell coordinate. Ceilings are the real grid limits and are
	/// named, not scattered as literals: 16384 columns (XFD) and 1048576 rows.
	/// Absolute markers are accepted on parse and dropped, because $ is a
	/// property of a reference, not of an address.
	/// </summary>
	public readonly struct CellAddress : IEquatable<CellAddress>
	{
		/// <summary>Column count ceiling. Last column is XFD.</summary>
		public const int MaxColumns = 16384;

		/// <summary>Row count ceiling.</summary>
		public const int MaxRows = 1048576;

		/// <summary>Longest legal column label: three letters.</summary>
		private const int MaxColumnLetters = 3;

		private CellAddress(int column, int row)
		{
			Column = column;
			Row = row;
		}

		/// <summary>Zero-based column index.</summary>
		public int Column { get; }

		/// <summary>Zero-based row index.</summary>
		public int Row { get; }

		/// <summary>Builds an address from zero-based indices.</summary>
		public static SheetStatus TryCreate(int column, int row, out CellAddress address)
		{
			address = default;

			if (column < 0 || row < 0)
			{
				return SheetStatus.ErrRange;
			}

			if (column >= MaxColumns || row >= MaxRows)
			{
				return SheetStatus.ErrLimit;
			}

			address = new CellAddress(column, row);
			return SheetStatus.Ok;
		}

		/// <summary>
		/// Parses an A1 address such as "B7", "$B$7" or "xfd1048576". Returns a
		/// status for every rejection instead of throwing or clamping.
		/// </summary>
		public static SheetStatus TryParse(string text, out CellAddress address)
		{
			address = default;

			if (text is null)
			{
				return SheetStatus.ErrNull;
			}

			int length = text.Length;
			if (length == 0)
			{
				return SheetStatus.ErrFormat;
			}

			int index = 0;

			if (text[index] == '$')
			{
				index++;
			}

			long column = 0;
			int letters = 0;

			while (index < length)
			{
				char c = text[index];
				int value;

				if (c >= 'A' && c <= 'Z')
				{
					value = c - 'A';
				}
				else if (c >= 'a' && c <= 'z')
				{
					value = c - 'a';
				}
				else
				{
					break;
				}

				letters++;
				if (letters > MaxColumnLetters)
				{
					return SheetStatus.ErrLimit;
				}

				column = (column * 26L) + value + 1L;
				index++;
			}

			if (letters == 0)
			{
				return SheetStatus.ErrFormat;
			}

			if (index < length && text[index] == '$')
			{
				index++;
			}

			long row = 0;
			int digits = 0;

			while (index < length)
			{
				char c = text[index];
				if (c < '0' || c > '9')
				{
					break;
				}

				row = (row * 10L) + (c - '0');
				digits++;
				index++;

				if (row > MaxRows)
				{
					return SheetStatus.ErrLimit;
				}
			}

			if (digits == 0)
			{
				return SheetStatus.ErrFormat;
			}

			if (index != length)
			{
				return SheetStatus.ErrFormat;
			}

			if (row == 0)
			{
				return SheetStatus.ErrRange;
			}

			if (column > MaxColumns)
			{
				return SheetStatus.ErrLimit;
			}

			address = new CellAddress((int)(column - 1L), (int)(row - 1L));
			return SheetStatus.Ok;
		}

		/// <summary>Writes the column label for a zero-based column index.</summary>
		public static SheetStatus TryFormatColumn(int column, out string label)
		{
			label = string.Empty;

			if (column < 0)
			{
				return SheetStatus.ErrRange;
			}

			if (column >= MaxColumns)
			{
				return SheetStatus.ErrLimit;
			}

			char[] buffer = new char[MaxColumnLetters];
			int position = MaxColumnLetters;
			int remaining = column + 1;

			while (remaining > 0)
			{
				int digit = (remaining - 1) % 26;
				position--;
				buffer[position] = (char)('A' + digit);
				remaining = (remaining - 1) / 26;
			}

			label = new string(buffer, position, MaxColumnLetters - position);
			return SheetStatus.Ok;
		}

		/// <summary>Renders the address in A1 form.</summary>
		public override string ToString()
		{
			string label;
			if (TryFormatColumn(Column, out label) != SheetStatus.Ok)
			{
				return "#REF!";
			}

			return label + (Row + 1).ToString(CultureInfo.InvariantCulture);
		}

		public bool Equals(CellAddress other)
		{
			return Column == other.Column && Row == other.Row;
		}

		public override bool Equals(object? obj)
		{
			return obj is CellAddress other && Equals(other);
		}

		public override int GetHashCode()
		{
			return (Row * 31) ^ Column;
		}

		public static bool operator ==(CellAddress left, CellAddress right)
		{
			return left.Equals(right);
		}

		public static bool operator !=(CellAddress left, CellAddress right)
		{
			return !left.Equals(right);
		}
	}
}
