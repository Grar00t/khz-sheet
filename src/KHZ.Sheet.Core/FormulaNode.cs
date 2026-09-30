using System;
using System.Collections.Generic;

namespace KHZ.Sheet.Core
{
	/// <summary>Shape of a parsed formula node.</summary>
	public enum FormulaNodeKind
	{
		Number = 0,
		Text = 1,
		Boolean = 2,
		Error = 3,
		Reference = 4,
		Name = 5,
		Missing = 6,
		Group = 7,
		Unary = 8,
		Postfix = 9,
		Binary = 10,
		Function = 11,
	}

	/// <summary>Base of the formula syntax tree.</summary>
	public abstract class FormulaNode
	{
		/// <summary>Which node this is, without a type test.</summary>
		public abstract FormulaNodeKind Kind { get; }
	}

    /// <summary>A numeric literal with its exact source spelling. Evaluation
    /// requires RawText; Value is retained only for syntax tooling compatibility.</summary>
	public sealed class NumberNode : FormulaNode
	{
        /// <summary>Legacy syntax-only constructor. Exact native lowering refuses
        /// nodes without source text, rather than guessing decimal intent.</summary>
		public NumberNode(double value)
		{
			Value = value;
			RawText = null;
		}

		/// <summary>
		/// Carries the literal as written alongside the double.
		///
		/// rawText is not validated or normalised here. It is the source text,
		/// and a parser that trimmed or reformatted it would reintroduce exactly
		/// the loss this constructor exists to avoid. An empty or whitespace
		/// string is treated as absent rather than stored, because it could not
		/// be parsed back into a number.
		/// </summary>
		public NumberNode(double value, string? rawText)
		{
			Value = value;
			RawText = string.IsNullOrWhiteSpace(rawText) ? null : rawText;
		}

		/// <summary>
		/// The literal as a double. Lossy for any value not representable in
		/// binary floating point; prefer RawText when it is present.
		/// </summary>
		public double Value { get; }

        /// <summary>Original digits, or null for a legacy syntax-only node.
        /// FormulaParser supplies this text without floating-point reconstruction.</summary>
		public string? RawText { get; }

		/// <summary>
		/// True when the exact text is available, so a caller can convert to an
		/// exact rational instead of reconstructing one from the double.
		/// </summary>
		public bool HasRawText
		{
			get { return RawText != null; }
		}

		public override FormulaNodeKind Kind
		{
			get { return FormulaNodeKind.Number; }
		}
	}

	/// <summary>A text literal, already unescaped.</summary>
	public sealed class TextNode : FormulaNode
	{
		public TextNode(string value)
		{
			Value = value;
		}

		public string Value { get; }

		public override FormulaNodeKind Kind
		{
			get { return FormulaNodeKind.Text; }
		}
	}

	/// <summary>TRUE or FALSE.</summary>
	public sealed class BooleanNode : FormulaNode
	{
		public BooleanNode(bool value)
		{
			Value = value;
		}

		public bool Value { get; }

		public override FormulaNodeKind Kind
		{
			get { return FormulaNodeKind.Boolean; }
		}
	}

	/// <summary>An error literal written directly in the formula.</summary>
	public sealed class ErrorNode : FormulaNode
	{
		public ErrorNode(string text)
		{
			Text = text;
		}

		public string Text { get; }

		public override FormulaNodeKind Kind
		{
			get { return FormulaNodeKind.Error; }
		}
	}

	/// <summary>A cell reference as written, sheet qualifier included.</summary>
	public sealed class ReferenceNode : FormulaNode
	{
		public ReferenceNode(string text)
		{
			Text = text;
		}

		public string Text { get; }

		public override FormulaNodeKind Kind
		{
			get { return FormulaNodeKind.Reference; }
		}
	}

	/// <summary>A bare name that is not a call: a defined name.</summary>
	public sealed class NameNode : FormulaNode
	{
		public NameNode(string text)
		{
			Text = text;
		}

		public string Text { get; }

		public override FormulaNodeKind Kind
		{
			get { return FormulaNodeKind.Name; }
		}
	}

	/// <summary>
	/// An omitted argument, as the middle slot of IF(A1,,2). It is a real
	/// position in the call and is kept, not dropped, because dropping it
	/// shifts every argument after it.
	/// </summary>
	public sealed class MissingNode : FormulaNode
	{
		public override FormulaNodeKind Kind
		{
			get { return FormulaNodeKind.Missing; }
		}
	}

	/// <summary>Explicit parentheses, kept so the text can be written back.</summary>
	public sealed class GroupNode : FormulaNode
	{
		public GroupNode(FormulaNode inner)
		{
			Inner = inner;
		}

		public FormulaNode Inner { get; }

		public override FormulaNodeKind Kind
		{
			get { return FormulaNodeKind.Group; }
		}
	}

	/// <summary>A prefix sign.</summary>
	public sealed class UnaryNode : FormulaNode
	{
		public UnaryNode(string op, FormulaNode operand)
		{
			Operator = op;
			Operand = operand;
		}

		public string Operator { get; }

		public FormulaNode Operand { get; }

		public override FormulaNodeKind Kind
		{
			get { return FormulaNodeKind.Unary; }
		}
	}

	/// <summary>The trailing percent sign.</summary>
	public sealed class PostfixNode : FormulaNode
	{
		public PostfixNode(string op, FormulaNode operand)
		{
			Operator = op;
			Operand = operand;
		}

		public string Operator { get; }

		public FormulaNode Operand { get; }

		public override FormulaNodeKind Kind
		{
			get { return FormulaNodeKind.Postfix; }
		}
	}

	/// <summary>
	/// A binary operation. The intersection operator carries the text " ",
	/// which is why the lexer keeps spaces.
	/// </summary>
	public sealed class BinaryNode : FormulaNode
	{
		public BinaryNode(string op, FormulaNode left, FormulaNode right)
		{
			Operator = op;
			Left = left;
			Right = right;
		}

		public string Operator { get; }

		public FormulaNode Left { get; }

		public FormulaNode Right { get; }

		public override FormulaNodeKind Kind
		{
			get { return FormulaNodeKind.Binary; }
		}
	}

	/// <summary>A call. The name is kept exactly as written.</summary>
	public sealed class FunctionNode : FormulaNode
	{
		public FunctionNode(string name, IReadOnlyList<FormulaNode> arguments)
		{
			Name = name;
			Arguments = arguments;
		}

		public string Name { get; }

		public IReadOnlyList<FormulaNode> Arguments { get; }

		public override FormulaNodeKind Kind
		{
			get { return FormulaNodeKind.Function; }
		}
	}
}
