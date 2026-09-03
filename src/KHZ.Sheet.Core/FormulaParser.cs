using System;
using System.Collections.Generic;

namespace KHZ.Sheet.Core
{
	/// <summary>
	/// Recursive descent parser over the lexer's tokens.
	///
	/// The precedence table is the real one, not a convenient one:
	/// range colon, then intersection, then the prefix sign, then percent,
	/// then caret, then multiply and divide, then add and subtract, then
	/// concatenation, then comparison. The prefix sign binds tighter than the
	/// caret, which is why -2^2 is 4 here and in Excel, and -4 in a language
	/// that copied the mathematical convention instead of measuring.
	/// </summary>
	public static class FormulaParser
	{
		private const int PrecedenceRange = 80;
		private const int PrecedenceIntersection = 70;
		private const int PrecedencePower = 50;
		private const int PrecedenceMultiply = 40;
		private const int PrecedenceAdd = 30;
		private const int PrecedenceConcat = 20;
		private const int PrecedenceCompare = 10;

		/// <summary>Lexes and parses in one step.</summary>
		public static SheetStatus TryParse(string formula, out FormulaNode? root)
		{
			root = null;

			IReadOnlyList<FormulaToken> tokens;
			SheetStatus status = FormulaLexer.TryTokenize(formula, out tokens);
			if (status != SheetStatus.Ok)
			{
				return status;
			}

			return TryParse(tokens, out root);
		}

		/// <summary>Parses an already lexed formula.</summary>
		public static SheetStatus TryParse(IReadOnlyList<FormulaToken> tokens, out FormulaNode? root)
		{
			root = null;

			if (tokens is null)
			{
				return SheetStatus.ErrNull;
			}

			State state = new State(tokens);

			FormulaNode? node;
			SheetStatus status = ParseExpression(state, 0, out node);
			if (status != SheetStatus.Ok)
			{
				return status;
			}

			state.SkipSpaces();
			if (!state.AtEnd)
			{
				return SheetStatus.ErrFormat;
			}

			root = node;
			return SheetStatus.Ok;
		}

		private static SheetStatus ParseExpression(State state, int minPrecedence, out FormulaNode? node)
		{
			node = null;

			FormulaNode? left;
			SheetStatus status = ParseUnary(state, out left);
			if (status != SheetStatus.Ok || left is null)
			{
				return status == SheetStatus.Ok ? SheetStatus.ErrFormat : status;
			}

			while (true)
			{
				int mark = state.Index;
				state.SkipSpaces();
				bool crossedSpace = state.Index != mark;

				if (state.AtEnd)
				{
					state.Index = mark;
					break;
				}

				FormulaToken token = state.Current;
				string op;
				int precedence;
				bool consumesToken;

				if (token.Kind == FormulaTokenKind.Colon)
				{
					op = ":";
					precedence = PrecedenceRange;
					consumesToken = true;
				}
				else if (token.Kind == FormulaTokenKind.Operator)
				{
					op = token.Text;
					precedence = BinaryPrecedence(op);
					consumesToken = true;

					if (precedence == 0)
					{
						state.Index = mark;
						break;
					}
				}
				else if (crossedSpace && IsOperandStart(token.Kind))
				{
					// Two operands with only whitespace between them: intersection.
					op = " ";
					precedence = PrecedenceIntersection;
					consumesToken = false;
				}
				else
				{
					state.Index = mark;
					break;
				}

				if (precedence < minPrecedence)
				{
					state.Index = mark;
					break;
				}

				if (consumesToken)
				{
					state.Index++;
				}

				FormulaNode? right;
				status = ParseExpression(state, precedence + 1, out right);
				if (status != SheetStatus.Ok)
				{
					return status;
				}

				if (right is null)
				{
					return SheetStatus.ErrFormat;
				}

				left = new BinaryNode(op, left, right);
			}

			node = left;
			return SheetStatus.Ok;
		}

		private static SheetStatus ParseUnary(State state, out FormulaNode? node)
		{
			node = null;
			state.SkipSpaces();

			if (state.AtEnd)
			{
				return SheetStatus.ErrFormat;
			}

			FormulaToken token = state.Current;

			if (token.Kind == FormulaTokenKind.Operator
				&& (string.Equals(token.Text, "-", StringComparison.Ordinal)
					|| string.Equals(token.Text, "+", StringComparison.Ordinal)))
			{
				state.Index++;

				FormulaNode? operand;
				SheetStatus status = ParseUnary(state, out operand);
				if (status != SheetStatus.Ok)
				{
					return status;
				}

				if (operand is null)
				{
					return SheetStatus.ErrFormat;
				}

				node = new UnaryNode(token.Text, operand);
				return SheetStatus.Ok;
			}

			return ParsePostfix(state, out node);
		}

		private static SheetStatus ParsePostfix(State state, out FormulaNode? node)
		{
			FormulaNode? current;
			SheetStatus status = ParsePrimary(state, out current);
			if (status != SheetStatus.Ok || current is null)
			{
				node = null;
				return status == SheetStatus.Ok ? SheetStatus.ErrFormat : status;
			}

			while (!state.AtEnd && state.Current.Kind == FormulaTokenKind.Percent)
			{
				state.Index++;
				current = new PostfixNode("%", current);
			}

			node = current;
			return SheetStatus.Ok;
		}

		private static SheetStatus ParsePrimary(State state, out FormulaNode? node)
		{
			node = null;
			state.SkipSpaces();

			if (state.AtEnd)
			{
				return SheetStatus.ErrFormat;
			}

			FormulaToken token = state.Current;

			switch (token.Kind)
			{
				case FormulaTokenKind.Number:
					state.Index++;
					node = new NumberNode(token.Number);
					return SheetStatus.Ok;

				case FormulaTokenKind.Text:
					state.Index++;
					node = new TextNode(token.Text);
					return SheetStatus.Ok;

				case FormulaTokenKind.Boolean:
					state.Index++;
					node = new BooleanNode(string.Equals(token.Text, "TRUE", StringComparison.OrdinalIgnoreCase));
					return SheetStatus.Ok;

				case FormulaTokenKind.Error:
					state.Index++;
					node = new ErrorNode(token.Text);
					return SheetStatus.Ok;

				case FormulaTokenKind.Reference:
					state.Index++;
					node = new ReferenceNode(token.Text);
					return SheetStatus.Ok;

				case FormulaTokenKind.Name:
				{
					state.Index++;

					// A call is a name immediately followed by an open paren. With a
					// space between them it is not a call, so no space is skipped here.
					if (!state.AtEnd && state.Current.Kind == FormulaTokenKind.OpenParen)
					{
						state.Index++;

						List<FormulaNode> arguments = new List<FormulaNode>();
						SheetStatus argumentStatus = ParseArguments(state, arguments);
						if (argumentStatus != SheetStatus.Ok)
						{
							return argumentStatus;
						}

						node = new FunctionNode(token.Text, arguments);
						return SheetStatus.Ok;
					}

					node = new NameNode(token.Text);
					return SheetStatus.Ok;
				}

				case FormulaTokenKind.OpenParen:
				{
					state.Index++;

					FormulaNode? inner;
					SheetStatus status = ParseExpression(state, 0, out inner);
					if (status != SheetStatus.Ok)
					{
						return status;
					}

					if (inner is null)
					{
						return SheetStatus.ErrFormat;
					}

					state.SkipSpaces();
					if (state.AtEnd || state.Current.Kind != FormulaTokenKind.CloseParen)
					{
						return SheetStatus.ErrFormat;
					}

					state.Index++;
					node = new GroupNode(inner);
					return SheetStatus.Ok;
				}

				default:
					return SheetStatus.ErrFormat;
			}
		}

		private static SheetStatus ParseArguments(State state, List<FormulaNode> arguments)
		{
			state.SkipSpaces();

			if (state.AtEnd)
			{
				return SheetStatus.ErrFormat;
			}

			if (state.Current.Kind == FormulaTokenKind.CloseParen)
			{
				state.Index++;
				return SheetStatus.Ok;
			}

			while (true)
			{
				state.SkipSpaces();

				if (state.AtEnd)
				{
					return SheetStatus.ErrFormat;
				}

				FormulaTokenKind kind = state.Current.Kind;

				if (kind == FormulaTokenKind.Comma || kind == FormulaTokenKind.CloseParen)
				{
					arguments.Add(new MissingNode());
				}
				else if (kind == FormulaTokenKind.Semicolon)
				{
					// A locale separator. The stored file format uses commas, so a
					// semicolon here is not translated on a guess.
					return SheetStatus.ErrUnsupported;
				}
				else
				{
					FormulaNode? argument;
					SheetStatus status = ParseExpression(state, 0, out argument);
					if (status != SheetStatus.Ok)
					{
						return status;
					}

					if (argument is null)
					{
						return SheetStatus.ErrFormat;
					}

					arguments.Add(argument);
				}

				state.SkipSpaces();

				if (state.AtEnd)
				{
					return SheetStatus.ErrFormat;
				}

				FormulaTokenKind next = state.Current.Kind;

				if (next == FormulaTokenKind.Comma)
				{
					state.Index++;
					continue;
				}

				if (next == FormulaTokenKind.CloseParen)
				{
					state.Index++;
					return SheetStatus.Ok;
				}

				if (next == FormulaTokenKind.Semicolon)
				{
					return SheetStatus.ErrUnsupported;
				}

				return SheetStatus.ErrFormat;
			}
		}

		private static int BinaryPrecedence(string op)
		{
			switch (op)
			{
				case "^":
					return PrecedencePower;
				case "*":
				case "/":
					return PrecedenceMultiply;
				case "+":
				case "-":
					return PrecedenceAdd;
				case "&":
					return PrecedenceConcat;
				case "=":
				case "<>":
				case "<":
				case "<=":
				case ">":
				case ">=":
					return PrecedenceCompare;
				default:
					return 0;
			}
		}

		private static bool IsOperandStart(FormulaTokenKind kind)
		{
			return kind == FormulaTokenKind.Number
				|| kind == FormulaTokenKind.Text
				|| kind == FormulaTokenKind.Boolean
				|| kind == FormulaTokenKind.Error
				|| kind == FormulaTokenKind.Reference
				|| kind == FormulaTokenKind.Name
				|| kind == FormulaTokenKind.OpenParen;
		}

		private sealed class State
		{
			private readonly IReadOnlyList<FormulaToken> _tokens;

			internal State(IReadOnlyList<FormulaToken> tokens)
			{
				_tokens = tokens;
				Index = 0;
			}

			internal int Index { get; set; }

			internal bool AtEnd
			{
				get { return Index >= _tokens.Count; }
			}

			internal FormulaToken Current
			{
				get { return _tokens[Index]; }
			}

			internal void SkipSpaces()
			{
				while (Index < _tokens.Count && _tokens[Index].Kind == FormulaTokenKind.Space)
				{
					Index++;
				}
			}
		}
	}
}
