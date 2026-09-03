using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KHZ.Sheet.Core
{
	/// <summary>
	/// Turns formula text into tokens. Hand written: no generator, no grammar
	/// file, no library. Every construct it does not implement is refused with
	/// a status code, never accepted and mistranslated.
	/// </summary>
	public static class FormulaLexer
	{
		/// <summary>Error literals the format defines. Nothing else is accepted after #.</summary>
		private static readonly string[] ErrorLiterals = new string[]
		{
			"#NULL!",
			"#DIV/0!",
			"#VALUE!",
			"#REF!",
			"#NAME?",
			"#NUM!",
			"#N/A",
			"#GETTING_DATA",
			"#SPILL!",
			"#CALC!",
		};

		/// <summary>
		/// Tokenises a formula. The leading equals sign is optional and is
		/// consumed if present.
		/// </summary>
		public static SheetStatus TryTokenize(string formula, out IReadOnlyList<FormulaToken> tokens)
		{
			tokens = Array.Empty<FormulaToken>();

			if (formula is null)
			{
				return SheetStatus.ErrNull;
			}

			List<FormulaToken> output = new List<FormulaToken>();
			int i = 0;
			int n = formula.Length;

			if (n > 0 && formula[0] == '=')
			{
				i = 1;
			}

			while (i < n)
			{
				char c = formula[i];

				if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
				{
					int start = i;
					while (i < n && (formula[i] == ' ' || formula[i] == '\t' || formula[i] == '\r' || formula[i] == '\n'))
					{
						i++;
					}

					output.Add(new FormulaToken(FormulaTokenKind.Space, " ", start, i - start, 0d));
					continue;
				}

				if (c == '"')
				{
					int start = i;
					i++;
					StringBuilder text = new StringBuilder();
					bool closed = false;

					while (i < n)
					{
						if (formula[i] == '"')
						{
							if (i + 1 < n && formula[i + 1] == '"')
							{
								text.Append('"');
								i += 2;
								continue;
							}

							i++;
							closed = true;
							break;
						}

						text.Append(formula[i]);
						i++;
					}

					if (!closed)
					{
						return SheetStatus.ErrFormat;
					}

					output.Add(new FormulaToken(FormulaTokenKind.Text, text.ToString(), start, i - start, 0d));
					continue;
				}

				if (c == '#')
				{
					string? literal = MatchErrorLiteral(formula, i);
					if (literal is null)
					{
						return SheetStatus.ErrFormat;
					}

					output.Add(new FormulaToken(FormulaTokenKind.Error, literal, i, literal.Length, 0d));
					i += literal.Length;
					continue;
				}

				if ((c >= '0' && c <= '9') || (c == '.' && i + 1 < n && formula[i + 1] >= '0' && formula[i + 1] <= '9'))
				{
					int start = i;

					while (i < n && formula[i] >= '0' && formula[i] <= '9')
					{
						i++;
					}

					if (i < n && formula[i] == '.')
					{
						i++;
						while (i < n && formula[i] >= '0' && formula[i] <= '9')
						{
							i++;
						}
					}

					if (i < n && (formula[i] == 'e' || formula[i] == 'E'))
					{
						int save = i;
						i++;

						if (i < n && (formula[i] == '+' || formula[i] == '-'))
						{
							i++;
						}

						if (i < n && formula[i] >= '0' && formula[i] <= '9')
						{
							while (i < n && formula[i] >= '0' && formula[i] <= '9')
							{
								i++;
							}
						}
						else
						{
							i = save;
						}
					}

					string literal = formula.Substring(start, i - start);
					double value;
					if (!double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
					{
						return SheetStatus.ErrFormat;
					}

					output.Add(new FormulaToken(FormulaTokenKind.Number, literal, start, i - start, value));
					continue;
				}

				if (c == '\'' || c == '$' || c == '_' || c == '\\' || char.IsLetter(c))
				{
					int start = i;

					if (c == '\'')
					{
						i++;
						bool closed = false;

						while (i < n)
						{
							if (formula[i] == '\'')
							{
								if (i + 1 < n && formula[i + 1] == '\'')
								{
									i += 2;
									continue;
								}

								i++;
								closed = true;
								break;
							}

							i++;
						}

						if (!closed || i >= n || formula[i] != '!')
						{
							return SheetStatus.ErrFormat;
						}

						i++;
					}

					int bodyStart = i;

					while (i < n)
					{
						char d = formula[i];
						if (char.IsLetterOrDigit(d) || d == '$' || d == '_' || d == '.' || d == '\\')
						{
							i++;
							continue;
						}

						break;
					}

					bool qualified = start != bodyStart;

					if (!qualified && i < n && formula[i] == '!')
					{
						// Unquoted sheet name, as in Sheet1!B7.
						i++;
						bodyStart = i;
						qualified = true;

						while (i < n)
						{
							char d = formula[i];
							if (char.IsLetterOrDigit(d) || d == '$' || d == '_' || d == '.')
							{
								i++;
								continue;
							}

							break;
						}
					}

					string whole = formula.Substring(start, i - start);
					string body = formula.Substring(bodyStart, i - bodyStart);

					if (!qualified)
					{
						if (string.Equals(whole, "TRUE", StringComparison.OrdinalIgnoreCase)
							|| string.Equals(whole, "FALSE", StringComparison.OrdinalIgnoreCase))
						{
							output.Add(new FormulaToken(FormulaTokenKind.Boolean, whole, start, i - start, 0d));
							continue;
						}
					}

					// A1 shaped text is a reference. Anything else, including a name
					// that is merely shaped like one past the grid ceiling such as
					// A1048577, stays a name. That is the real rule, not a shortcut.
					CellAddress ignored;
					FormulaTokenKind kind = CellAddress.TryParse(body, out ignored) == SheetStatus.Ok
						? FormulaTokenKind.Reference
						: FormulaTokenKind.Name;

					if (qualified && kind == FormulaTokenKind.Name)
					{
						kind = FormulaTokenKind.Reference;
					}

					output.Add(new FormulaToken(kind, whole, start, i - start, 0d));
					continue;
				}

				switch (c)
				{
					case '(':
						output.Add(new FormulaToken(FormulaTokenKind.OpenParen, "(", i, 1, 0d));
						i++;
						continue;

					case ')':
						output.Add(new FormulaToken(FormulaTokenKind.CloseParen, ")", i, 1, 0d));
						i++;
						continue;

					case ',':
						output.Add(new FormulaToken(FormulaTokenKind.Comma, ",", i, 1, 0d));
						i++;
						continue;

					case ';':
						output.Add(new FormulaToken(FormulaTokenKind.Semicolon, ";", i, 1, 0d));
						i++;
						continue;

					case ':':
						output.Add(new FormulaToken(FormulaTokenKind.Colon, ":", i, 1, 0d));
						i++;
						continue;

					case '%':
						output.Add(new FormulaToken(FormulaTokenKind.Percent, "%", i, 1, 0d));
						i++;
						continue;

					case '+':
					case '-':
					case '*':
					case '/':
					case '^':
					case '&':
					case '=':
						output.Add(new FormulaToken(FormulaTokenKind.Operator, c.ToString(), i, 1, 0d));
						i++;
						continue;

					case '<':
						if (i + 1 < n && (formula[i + 1] == '=' || formula[i + 1] == '>'))
						{
							output.Add(new FormulaToken(FormulaTokenKind.Operator, formula.Substring(i, 2), i, 2, 0d));
							i += 2;
							continue;
						}

						output.Add(new FormulaToken(FormulaTokenKind.Operator, "<", i, 1, 0d));
						i++;
						continue;

					case '>':
						if (i + 1 < n && formula[i + 1] == '=')
						{
							output.Add(new FormulaToken(FormulaTokenKind.Operator, ">=", i, 2, 0d));
							i += 2;
							continue;
						}

						output.Add(new FormulaToken(FormulaTokenKind.Operator, ">", i, 1, 0d));
						i++;
						continue;

					case '{':
					case '}':
						// Array literal. Not implemented, so not accepted.
						return SheetStatus.ErrUnsupported;

					case '[':
					case ']':
						// Table reference or external workbook. Same rule.
						return SheetStatus.ErrUnsupported;

					default:
						return SheetStatus.ErrFormat;
				}
			}

			tokens = output;
			return SheetStatus.Ok;
		}

		private static string? MatchErrorLiteral(string formula, int index)
		{
			for (int k = 0; k < ErrorLiterals.Length; k++)
			{
				string candidate = ErrorLiterals[k];
				if (index + candidate.Length <= formula.Length
					&& string.Compare(formula, index, candidate, 0, candidate.Length, StringComparison.OrdinalIgnoreCase) == 0)
				{
					return candidate;
				}
			}

			return null;
		}
	}
}
