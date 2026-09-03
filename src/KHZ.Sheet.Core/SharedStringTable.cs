using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;

namespace KHZ.Sheet.Core
{
	/// <summary>
	/// The xl/sharedStrings.xml table, read as a positional list.
	///
	/// Rich-text runs are concatenated because the run boundary carries only
	/// formatting, and formatting is outside this engine. Phonetic runs (rPh)
	/// are excluded: they are a reading aid, not part of the cell value, and
	/// including them would silently corrupt every Japanese workbook.
	/// </summary>
	public static class SharedStringTable
	{
		/// <summary>Canonical part name inside the package.</summary>
		public const string PartName = "xl/sharedStrings.xml";

		/// <summary>
		/// Reads the table. A workbook with no shared strings is not an error;
		/// the caller checks for the part before calling.
		/// </summary>
		public static SheetStatus TryLoad(Stream content, out IReadOnlyList<string> strings)
		{
			strings = Array.Empty<string>();

			if (content is null)
			{
				return SheetStatus.ErrNull;
			}

			List<string> items = new List<string>();
			StringBuilder builder = new StringBuilder();

			XmlReaderSettings settings = new XmlReaderSettings();
			settings.IgnoreComments = true;
			settings.IgnoreProcessingInstructions = true;
			settings.IgnoreWhitespace = false;
			// No document type, no resolver: an XML file cannot make this process
			// open a handle to anything off this machine.
			settings.DtdProcessing = DtdProcessing.Prohibit;
			settings.XmlResolver = null;
			settings.CloseInput = false;

			bool inItem = false;
			bool inPhonetic = false;
			bool capturing = false;

			try
			{
				using (XmlReader reader = XmlReader.Create(content, settings))
				{
					while (reader.Read())
					{
						switch (reader.NodeType)
						{
							case XmlNodeType.Element:
								if (string.Equals(reader.LocalName, "si", StringComparison.Ordinal))
								{
									builder.Clear();
									inPhonetic = false;
									capturing = false;

									if (reader.IsEmptyElement)
									{
										items.Add(string.Empty);
										inItem = false;
									}
									else
									{
										inItem = true;
									}
								}
								else if (string.Equals(reader.LocalName, "rPh", StringComparison.Ordinal))
								{
									if (!reader.IsEmptyElement)
									{
										inPhonetic = true;
									}
								}
								else if (string.Equals(reader.LocalName, "t", StringComparison.Ordinal))
								{
									if (inItem && !inPhonetic && !reader.IsEmptyElement)
									{
										capturing = true;
									}
								}

								break;

							case XmlNodeType.Text:
							case XmlNodeType.CDATA:
							case XmlNodeType.Whitespace:
							case XmlNodeType.SignificantWhitespace:
								if (capturing)
								{
									builder.Append(reader.Value);
								}

								break;

							case XmlNodeType.EndElement:
								if (string.Equals(reader.LocalName, "t", StringComparison.Ordinal))
								{
									capturing = false;
								}
								else if (string.Equals(reader.LocalName, "rPh", StringComparison.Ordinal))
								{
									inPhonetic = false;
								}
								else if (string.Equals(reader.LocalName, "si", StringComparison.Ordinal))
								{
									if (inItem)
									{
										items.Add(builder.ToString());
										inItem = false;
									}
								}

								break;
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

			strings = items;
			return SheetStatus.Ok;
		}
	}
}
