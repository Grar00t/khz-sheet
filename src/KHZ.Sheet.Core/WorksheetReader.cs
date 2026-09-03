using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Text;
using System.Xml;

namespace KHZ.Sheet.Core
{
	/// <summary>How a cell's stored text is to be read.</summary>
	public enum CellValueKind
	{
		/// <summary>No t attribute: a number. This is the file format default.</summary>
		Number = 0,

		/// <summary>t="s": the value is an index into the shared string table.</summary>
		SharedString = 1,

		/// <summary>t="str": a formula that produced text.</summary>
		FormulaString = 2,

		/// <summary>t="inlineStr": text stored in the cell itself.</summary>
		InlineString = 3,

		/// <summary>t="b": 0 or 1.</summary>
		Boolean = 4,

		/// <summary>t="e": an error literal such as #DIV/0!.</summary>
		Error = 5,

		/// <summary>t="d": an ISO 8601 date.</summary>
		Date = 6,

		/// <summary>A t value this engine does not know. Reported, not guessed.</summary>
		Unknown = 7,
	}

	/// <summary>
	/// One cell as it is stored on disk. Both the formula and the value Excel
	/// last computed are kept, because the stored value is the only independent
	/// witness available to judge a recalculation engine. An engine that
	/// overwrites it before comparing is grading its own paper.
	/// </summary>
	public sealed class SheetCell
	{
		internal SheetCell(
			CellAddress address,
			CellValueKind kind,
			string? rawType,
			string? storedValue,
			string? formula,
			bool sharedFormula,
			int sharedFormulaIndex)
		{
			Address = address;
			Kind = kind;
			RawType = rawType;
			StoredValue = storedValue;
			Formula = formula;
			SharedFormula = sharedFormula;
			SharedFormulaIndex = sharedFormulaIndex;
		}

		/// <summary>Where the cell sits.</summary>
		public CellAddress Address { get; }

		/// <summary>Interpretation of <see cref="StoredValue"/>.</summary>
		public CellValueKind Kind { get; }

		/// <summary>The literal t attribute, kept when Kind is Unknown.</summary>
		public string? RawType { get; }

		/// <summary>Text of v, or of is/t for an inline string. Null when absent.</summary>
		public string? StoredValue { get; }

		/// <summary>Formula text without the leading equals sign. Null when absent.</summary>
		public string? Formula { get; }

		/// <summary>
		/// True when the cell points at a shared formula defined elsewhere. This
		/// engine records the pointer and does not translate it; a shared formula
		/// that is guessed at is wrong in every cell but the first.
		/// </summary>
		public bool SharedFormula { get; }

		/// <summary>The si index of a shared formula, or -1.</summary>
		public int SharedFormulaIndex { get; }
	}

	/// <summary>Streaming reader for one xl/worksheets/sheetN.xml part.</summary>
	public static class WorksheetReader
	{
		private const int NoSharedFormula = -1;

		/// <summary>
		/// Reads every populated cell. A cell without an r attribute stops the
		/// read with ErrFormat rather than being placed by counting, because a
		/// cell placed by inference lands in the wrong column in any sparse row.
		/// </summary>
		public static SheetStatus TryLoad(Stream content, out IReadOnlyList<SheetCell> cells)
		{
			cells = Array.Empty<SheetCell>();

			if (content is null)
			{
				return SheetStatus.ErrNull;
			}

			List<SheetCell> found = new List<SheetCell>();
			StringBuilder valueText = new StringBuilder();
			StringBuilder formulaText = new StringBuilder();

			XmlReaderSettings settings = new XmlReaderSettings();
			settings.IgnoreComments = true;
			settings.IgnoreProcessingInstructions = true;
			settings.IgnoreWhitespace = false;
			settings.DtdProcessing = DtdProcessing.Prohibit;
			settings.XmlResolver = null;
			settings.CloseInput = false;

			bool inSheetData = false;
			bool inCell = false;
			bool inInline = false;
			bool hasValue = false;
			bool hasFormula = false;
			int capture = 0;

			CellAddress address = default;
			CellValueKind kind = CellValueKind.Number;
			string? rawType = null;
			bool sharedFormula = false;
			int sharedIndex = NoSharedFormula;

			try
			{
				using (XmlReader reader = XmlReader.Create(content, settings))
				{
					while (reader.Read())
					{
						switch (reader.NodeType)
						{
							case XmlNodeType.Element:
							{
								string name = reader.LocalName;

								if (string.Equals(name, "sheetData", StringComparison.Ordinal))
								{
									inSheetData = !reader.IsEmptyElement;
								}
								else if (inSheetData && string.Equals(name, "c", StringComparison.Ordinal))
								{
									string? reference = reader.GetAttribute("r");
									if (reference is null)
									{
										return SheetStatus.ErrFormat;
									}

									SheetStatus parsed = CellAddress.TryParse(reference, out address);
									if (parsed != SheetStatus.Ok)
									{
										return parsed;
									}

									rawType = reader.GetAttribute("t");
									kind = ClassifyType(rawType);

									valueText.Clear();
									formulaText.Clear();
									hasValue = false;
									hasFormula = false;
									sharedFormula = false;
									sharedIndex = NoSharedFormula;
									capture = 0;
									inInline = false;

									if (reader.IsEmptyElement)
									{
										// A styled but empty cell. Recorded, since its
										// presence is part of the sheet.
										found.Add(new SheetCell(address, kind, rawType, null, null, false, NoSharedFormula));
										inCell = false;
									}
									else
									{
										inCell = true;
									}
								}
								else if (inCell && string.Equals(name, "v", StringComparison.Ordinal))
								{
									hasValue = true;
									if (!reader.IsEmptyElement)
									{
										capture = 1;
									}
								}
								else if (inCell && string.Equals(name, "f", StringComparison.Ordinal))
								{
									hasFormula = true;

									string? formulaType = reader.GetAttribute("t");
									if (string.Equals(formulaType, "shared", StringComparison.Ordinal))
									{
										sharedFormula = true;

										string? si = reader.GetAttribute("si");
										int index;
										if (si is not null
											&& int.TryParse(si, NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
										{
											sharedIndex = index;
										}
									}

									if (!reader.IsEmptyElement)
									{
										capture = 2;
									}
								}
								else if (inCell && string.Equals(name, "is", StringComparison.Ordinal))
								{
									inInline = !reader.IsEmptyElement;
								}
								else if (inCell && inInline && string.Equals(name, "t", StringComparison.Ordinal))
								{
									hasValue = true;
									if (!reader.IsEmptyElement)
									{
										capture = 1;
									}
								}

								break;
							}

							case XmlNodeType.Text:
							case XmlNodeType.CDATA:
							case XmlNodeType.Whitespace:
							case XmlNodeType.SignificantWhitespace:
							{
								if (capture == 1)
								{
									valueText.Append(reader.Value);
								}
								else if (capture == 2)
								{
									formulaText.Append(reader.Value);
								}

								break;
							}

							case XmlNodeType.EndElement:
							{
								string name = reader.LocalName;

								if (string.Equals(name, "v", StringComparison.Ordinal)
									|| string.Equals(name, "f", StringComparison.Ordinal)
									|| string.Equals(name, "t", StringComparison.Ordinal))
								{
									capture = 0;
								}
								else if (string.Equals(name, "is", StringComparison.Ordinal))
								{
									inInline = false;
								}
								else if (string.Equals(name, "c", StringComparison.Ordinal))
								{
									if (inCell)
									{
										found.Add(new SheetCell(
											address,
											kind,
											rawType,
											hasValue ? valueText.ToString() : null,
											hasFormula ? formulaText.ToString() : null,
											sharedFormula,
											sharedIndex));

										inCell = false;
									}
								}
								else if (string.Equals(name, "sheetData", StringComparison.Ordinal))
								{
									inSheetData = false;
								}

								break;
							}
						}
					}
				}
			}
			catch (XmlException)
			{
				return SheetStatus.ErrFormat;
			}
			catch (IOException)
			{
				return SheetStatus.ErrFormat;
			}

			cells = found;
			return SheetStatus.Ok;
		}

		private static CellValueKind ClassifyType(string? rawType)
		{
			if (rawType is null || string.Equals(rawType, "n", StringComparison.Ordinal))
			{
				return CellValueKind.Number;
			}

			if (string.Equals(rawType, "s", StringComparison.Ordinal))
			{
				return CellValueKind.SharedString;
			}

			if (string.Equals(rawType, "str", StringComparison.Ordinal))
			{
				return CellValueKind.FormulaString;
			}

			if (string.Equals(rawType, "inlineStr", StringComparison.Ordinal))
			{
				return CellValueKind.InlineString;
			}

			if (string.Equals(rawType, "b", StringComparison.Ordinal))
			{
				return CellValueKind.Boolean;
			}

			if (string.Equals(rawType, "e", StringComparison.Ordinal))
			{
				return CellValueKind.Error;
			}

			if (string.Equals(rawType, "d", StringComparison.Ordinal))
			{
				return CellValueKind.Date;
			}

			return CellValueKind.Unknown;
		}
	}
}
