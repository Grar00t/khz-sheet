using System;

namespace KHZ.Sheet.Core
{
	/// <summary>Lexical class of one piece of a formula.</summary>
	public enum FormulaTokenKind
	{
		/// <summary>A numeric literal.</summary>
		Number = 0,

		/// <summary>A quoted text literal, already unescaped.</summary>
		Text = 1,

		/// <summary>TRUE or FALSE.</summary>
		Boolean = 2,

		/// <summary>An error literal such as #DIV/0!.</summary>
		Error = 3,

		/// <summary>A cell reference, possibly sheet qualified.</summary>
		Reference = 4,

		/// <summary>A bare name: function name or defined name.</summary>
		Name = 5,

		/// <summary>One of + - * / ^ &amp; = &lt;&gt; &lt; &lt;= &gt; &gt;=</summary>
		Operator = 6,

		/// <summary>The postfix percent sign.</summary>
		Percent = 7,

		/// <summary>Opening parenthesis.</summary>
		OpenParen = 8,

		/// <summary>Closing parenthesis.</summary>
		CloseParen = 9,

		/// <summary>Argument separator.</summary>
		Comma = 10,

		/// <summary>Range operator.</summary>
		Colon = 11,

		/// <summary>Union operator, the semicolon in some locales.</summary>
		Semicolon = 12,

		/// <summary>
		/// Run of spaces. Kept rather than discarded: in Excel a space between
		/// two references is the intersection operator, so silently dropping it
		/// turns a real operator into nothing and changes the answer.
		/// </summary>
		Space = 13,
	}

	/// <summary>One token with its position in the source text.</summary>
	public sealed class FormulaToken
	{
		internal FormulaToken(FormulaTokenKind kind, string text, int start, int length, double number)
		{
			Kind = kind;
			Text = text;
			Start = start;
			Length = length;
			Number = number;
		}

		/// <summary>Lexical class.</summary>
		public FormulaTokenKind Kind { get; }

		/// <summary>
		/// Token text. For <see cref="FormulaTokenKind.Text"/> the surrounding
		/// quotes are removed and doubled quotes are collapsed.
		/// </summary>
		public string Text { get; }

		/// <summary>Zero-based offset into the formula string.</summary>
		public int Start { get; }

		/// <summary>Length in the source text, including quotes.</summary>
		public int Length { get; }

		/// <summary>Parsed value for <see cref="FormulaTokenKind.Number"/>, else 0.</summary>
		public double Number { get; }

		/// <inheritdoc />
		public override string ToString()
		{
			return Kind.ToString() + "(" + Text + ")";
		}
	}
}
