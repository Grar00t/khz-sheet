using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using KHZ.Sheet.Core;

namespace KHZ.Sheet.Tests
{
	internal static class Program
	{
		private static int failures;

		private static void Expect(bool condition, string message)
		{
			Console.WriteLine((condition ? "  OK   " : "  FAIL ") + message);
			if (!condition)
			{
				failures++;
			}
		}

		private static void AddEntry(ZipArchive archive, string name, byte[] bytes)
		{
			ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
			using Stream stream = entry.Open();
			stream.Write(bytes, 0, bytes.Length);
		}

		private static MemoryStream BuildPackage(bool includeEmpty)
		{
			MemoryStream stream = new MemoryStream();
			using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
			{
				AddEntry(archive, OpcPackage.ContentTypesPart, new byte[] { 0x3c, 0x54, 0x2f, 0x3e });
				AddEntry(archive, "xl/workbook.xml", new byte[] { 1, 2, 3, 4, 5 });
				AddEntry(archive, "custom/opaque.bin", new byte[] { 0, 255, 17, 34, 51, 68 });
				archive.CreateEntry("xl/");
				AddEntry(archive, "xl/duplicate.bin", new byte[] { 10, 11, 12 });
				AddEntry(archive, "xl/duplicate.bin", new byte[] { 90, 91, 92 });
				if (includeEmpty)
				{
					AddEntry(archive, "xl/empty.xml", Array.Empty<byte>());
				}
			}
			stream.Position = 0;
			return stream;
		}

		private static bool BytesEqual(Stream? stream, byte[] expected)
		{
			if (stream is null)
			{
				return false;
			}

			using (stream)
			using (MemoryStream copy = new MemoryStream())
			{
				stream.CopyTo(copy);
				byte[] actual = copy.ToArray();
				if (actual.Length != expected.Length)
				{
					return false;
				}
				for (int i = 0; i < actual.Length; ++i)
				{
					if (actual[i] != expected[i])
					{
						return false;
					}
				}
				return true;
			}
		}

		private static bool Contains(IReadOnlyList<string> values, string expected)
		{
			for (int i = 0; i < values.Count; ++i)
			{
				if (string.Equals(values[i], expected, StringComparison.Ordinal))
				{
					return true;
				}
			}
			return false;
		}

		private static void RoundTrip()
		{
			byte[] contentTypes = new byte[] { 0x3c, 0x54, 0x2f, 0x3e };
			byte[] opaque = new byte[] { 0, 255, 17, 34, 51, 68 };
			byte[] firstDuplicate = new byte[] { 10, 11, 12 };
			byte[] replacement = new byte[] { 9, 8, 7, 6 };
			byte[] replacementExpected = new byte[] { 9, 8, 7, 6 };

			using MemoryStream source = BuildPackage(includeEmpty: false);
			SheetStatus opened = OpcPackage.TryOpen(source, out OpcPackage? package);
			Expect(opened == SheetStatus.Ok && package != null, "open package");
			if (package is null)
			{
				return;
			}

			using (package)
			{
				Expect(package.HasContentTypes, "content types present");
				Expect(package.PartNames.Count == 4, "four carried parts");
				Expect(package.DroppedEntries.Count == 2, "directory and duplicate reported");
				Expect(Contains(package.DroppedEntries, "xl/"), "directory entry reported");
				Expect(Contains(package.DroppedEntries, "xl/duplicate.bin"), "duplicate entry reported");

				Expect(package.TryOpenPart("custom/opaque.bin", out Stream? beforeOpaque) == SheetStatus.Ok
					&& BytesEqual(beforeOpaque, opaque), "opaque payload retained on open");
				Expect(package.TryOpenPart("xl/duplicate.bin", out Stream? duplicate) == SheetStatus.Ok
					&& BytesEqual(duplicate, firstDuplicate), "first duplicate retained deterministically");

				Expect(package.TryReplacePart("xl/workbook.xml", replacement) == SheetStatus.Ok,
					"replace workbook payload");
				replacement[0] = 0;
				Expect(package.TryOpenPart("xl/workbook.xml", out Stream? owned) == SheetStatus.Ok
					&& BytesEqual(owned, replacementExpected), "replacement bytes are package-owned");

				using MemoryStream saved = new MemoryStream();
				Expect(package.TrySave(saved) == SheetStatus.Ok, "save package");
				saved.Position = 0;

				SheetStatus reopenedStatus = OpcPackage.TryOpen(saved, out OpcPackage? reopened);
				Expect(reopenedStatus == SheetStatus.Ok && reopened != null, "reopen saved package");
				if (reopened != null)
				{
					using (reopened)
					{
						Expect(reopened.DroppedEntries.Count == 0, "saved container has no hidden drops");
						Expect(reopened.PartNames.Count == 4, "part order/count preserved");
						Expect(reopened.TryOpenPart(OpcPackage.ContentTypesPart, out Stream? ct) == SheetStatus.Ok
							&& BytesEqual(ct, contentTypes), "untouched content-types payload byte-identical");
						Expect(reopened.TryOpenPart("custom/opaque.bin", out Stream? afterOpaque) == SheetStatus.Ok
							&& BytesEqual(afterOpaque, opaque), "untouched opaque payload byte-identical");
						Expect(reopened.TryOpenPart("xl/duplicate.bin", out Stream? afterDuplicate) == SheetStatus.Ok
							&& BytesEqual(afterDuplicate, firstDuplicate), "retained duplicate payload byte-identical");
						Expect(reopened.TryOpenPart("xl/workbook.xml", out Stream? afterWorkbook) == SheetStatus.Ok
							&& BytesEqual(afterWorkbook, replacementExpected), "only requested payload changed");
					}
				}
			}
		}

		private static void EmptyPartIsRefused()
		{
			using MemoryStream source = BuildPackage(includeEmpty: true);
			SheetStatus opened = OpcPackage.TryOpen(source, out OpcPackage? package);
			Expect(opened == SheetStatus.Ok && package != null, "open package containing empty part");
			if (package is null)
			{
				return;
			}

			using (package)
			using (MemoryStream destination = new MemoryStream())
			{
				Expect(package.TrySave(destination) == SheetStatus.ErrFormat,
					"empty part refused at save boundary");
			}
		}

		private static int Main()
		{
			Console.WriteLine("OPC payload fidelity:");
			RoundTrip();
			Console.WriteLine("OPC invalid carried part:");
			EmptyPartIsRefused();
			Console.WriteLine(failures == 0 ? "ALL PASS failures=0" : "FAIL failures=" + failures);
			return failures == 0 ? 0 : 1;
		}
	}
}
