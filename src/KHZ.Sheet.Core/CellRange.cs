using System;

namespace KHZ.Sheet.Core
{
	/// <summary>
	/// A rectangular block of cells, normalised so TopLeft is always the minimum
	/// corner. Whole-column ("A:A") and whole-row ("1:1") references are a
	/// declared gap: they are rejected with <see cref="SheetStatus.ErrUnsupported"/>
	/// rather than silently widened to the grid ceiling, because a reference that
	/// quietly becomes 1048576 rows is a wrong answer that looks like a right one.
	/// </summary>
	public readonly struct CellRange : IEquatable<CellRange>
	{
		private CellRange(CellAddress topLeft, CellAddress bottomRight)
		{
			TopLeft = topLeft;
			BottomRight = bottomRight;
		}

		/// <summary>Minimum corner.</summary>
		public CellAddress TopLeft { get; }

		/// <summary>Maximum corner, inclusive.</summary>
		public CellAddress BottomRight { get; }

		/// <summary>Column span, at least 1.</summary>
		public int ColumnCount
		{
			get { return BottomRight.Column - TopLeft.Column + 1; }
		}

		/// <summary>Row span, at least 1.</summary>
		public int RowCount
		{
			get { return BottomRight.Row - TopLeft.Row + 1; }
		}

		/// <summary>Cell count. Long because a full grid overflows int.</summary>
		public long CellCount
		{
			get { return (long)ColumnCount * RowCount; }
		}

		/// <summary>Builds a range from two corners in any order.</summary>
		public static SheetStatus TryCreate(CellAddress first, CellAddress second, out CellRange range)
		{
			range = default;

			int left = first.Column < second.Column ? first.Column : second.Column;
			int right = first.Column > second.Column ? first.Column : second.Column;
			int top = first.Row < second.Row ? first.Row : second.Row;
			int bottom = first.Row > second.Row ? first.Row : second.Row;

			CellAddress topLeft;
			SheetStatus status = CellAddress.TryCreate(left, top, out topLeft);
			if (status != SheetStatus.Ok)
			{
				return status;
			}

			CellAddress bottomRight;
			status = CellAddress.TryCreate(right, bottom, out bottomRight);
			if (status != SheetStatus.Ok)
			{
				return status;
			}

			range = new CellRange(topLeft, bottomRight);
			return SheetStatus.Ok;
		}

		/// <summary>
		/// Parses "A1:B7", "$A$1:$B$7" or a single address "A1" as a one-cell range.
		/// </summary>
		public static SheetStatus TryParse(string text, out CellRange range)
		{
			range = default;

			if (text is null)
			{
				return SheetStatus.ErrNull;
			}

			if (text.Length == 0)
			{
				return SheetStatus.ErrFormat;
			}

			int colon = text.IndexOf(':');
			if (colon < 0)
			{
				SheetStatus single = ClassifySegment(text);
				if (single != SheetStatus.Ok)
				{
					return single;
				}

				CellAddress only;
				SheetStatus parsed = CellAddress.TryParse(text, out only);
				if (parsed != SheetStatus.Ok)
				{
					return parsed;
				}

				return TryCreate(only, only, out range);
			}

			if (text.IndexOf(':', colon + 1) >= 0)
			{
				return SheetStatus.ErrFormat;
			}

			string leftText = text.Substring(0, colon);
			string rightText = text.Substring(colon + 1);

			SheetStatus leftKind = ClassifySegment(leftText);
			if (leftKind != SheetStatus.Ok)
			{
				return leftKind;
			}

			SheetStatus rightKind = ClassifySegment(rightText);
			if (rightKind != SheetStatus.Ok)
			{
				return rightKind;
			}

			CellAddress first;
			SheetStatus status = CellAddress.TryParse(leftText, out first);
			if (status != SheetStatus.Ok)
			{
				return status;
			}

			CellAddress second;
			status = CellAddress.TryParse(rightText, out second);
			if (status != SheetStatus.Ok)
			{
				return status;
			}

			return TryCreate(first, second, out range);
		}

		/// <summary>True when the address falls inside this range.</summary>
		public bool Contains(CellAddress address)
		{
			return address.Column >= TopLeft.Column
				&& address.Column <= BottomRight.Column
				&& address.Row >= TopLeft.Row
				&& address.Row <= BottomRight.Row;
		}

		/// <summary>True when the two ranges share at least one cell.</summary>
		public bool Intersects(CellRange other)
		{
			return TopLeft.Column <= other.BottomRight.Column
				&& BottomRight.Column >= other.TopLeft.Column
				&& TopLeft.Row <= other.BottomRight.Row
				&& BottomRight.Row >= other.TopLeft.Row;
		}

		/// <summary>
		/// Decides whether a reference segment is a cell address, or one of the
		/// two forms this engine refuses to guess at.
		/// </summary>
		private static SheetStatus ClassifySegment(string segment)
		{
			if (segment.Length == 0)
			{
				return SheetStatus.ErrFormat;
			}

			bool sawLetter = false;
			bool sawDigit = false;

			for (int i = 0; i < segment.Length; i++)
			{
				char c = segment[i];

				if (c == '$')
				{
					continue;
				}

				if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
				{
					sawLetter = true;
					continue;
				}

				if (c >= '0' && c <= '9')
				{
					sawDigit = true;
					continue;
				}

				return SheetStatus.ErrFormat;
			}

			if (sawLetter && !sawDigit)
			{
				// "A" in "A:A" - a whole column.
				return SheetStatus.ErrUnsupported;
			}

			if (sawDigit && !sawLetter)
			{
				// "1" in "1:1" - a whole row.
				return SheetStatus.ErrUnsupported;
			}

			if (!sawLetter && !sawDigit)
			{
				return SheetStatus.ErrFormat;
			}

			return SheetStatus.Ok;
		}

		/// <summary>Renders the range in A1 form, collapsing a single cell.</summary>
		public override string ToString()
		{
			if (TopLeft == BottomRight)
			{
				return TopLeft.ToString();
			}

			return TopLeft.ToString() + ":" + BottomRight.ToString();
		}

		public bool Equals(CellRange other)
		{
			return TopLeft == other.TopLeft && BottomRight == other.BottomRight;
		}

		public override bool Equals(object? obj)
		{
			return obj is CellRange other && Equals(other);
		}

		public override int GetHashCode()
		{
			return (TopLeft.GetHashCode() * 397) ^ BottomRight.GetHashCode();
		}

		public static bool operator ==(CellRange left, CellRange right)
		{
			return left.Equals(right);
		}

		public static bool operator !=(CellRange left, CellRange right)
		{
			return !left.Equals(right);
		}
	}
}
