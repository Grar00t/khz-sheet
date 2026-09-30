using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace KHZ.Sheet.Core
{
	/// <summary>
	/// An XLSX container held as an ordered list of parts and their exact bytes.
	/// An OPC package is a ZIP, and System.IO.Compression is in the base class
	/// library, so this needs no package reference, no feed and no network.
	///
	/// The contract: a part this engine was not asked to change is written back
	/// byte for byte. Nothing is lost by being unrepresented.
	/// </summary>
	public sealed class OpcPackage : IDisposable
	{
		/// <summary>The one part every valid OPC package must carry.</summary>
		public const string ContentTypesPart = "[Content_Types].xml";

		private const int CopyBufferSize = 81920;
        public const long MaxArchiveBytes = 128L * 1024 * 1024;
        public const long MaxExpandedBytes = 128L * 1024 * 1024;
        public const int MaxPartBytes = 16 * 1024 * 1024;
        public const int MaxEntries = 4096;
        private static bool SafeName(string name) => name.Length is > 0 and <= 512 &&
            !name.StartsWith("/", StringComparison.Ordinal) && !name.Contains('\\') && !name.Contains(':') &&
            !name.Contains('%') && Array.TrueForAll(name.Split('/'), p => p.Length > 0 && p != "." && p != "..");
        private static void CopyBounded(Stream source, Stream target, long limit)
        {
            byte[] buffer = new byte[CopyBufferSize]; long total = 0; int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0) {
                total += read;
                if (total > limit) throw new InvalidDataException("OPC resource limit");
                target.Write(buffer, 0, read);
            }
        }

		private readonly List<string> _order = new List<string>();

		private readonly Dictionary<string, byte[]> _parts =
			new Dictionary<string, byte[]>(StringComparer.Ordinal);

		private readonly List<string> _droppedEntries = new List<string>();

		private bool _disposed;

		private OpcPackage()
		{
		}

		/// <summary>Part names in container order.</summary>
		public IReadOnlyList<string> PartNames
		{
			get { return _order; }
		}

		/// <summary>
		/// Container entries that were not carried over: directory entries and
		/// duplicate names. Reported, never hidden - the parts round-trip exactly,
		/// the container may not.
		/// </summary>
		public IReadOnlyList<string> DroppedEntries
		{
			get { return _droppedEntries; }
		}

		/// <summary>False means this is not a valid OPC package.</summary>
		public bool HasContentTypes
		{
			get { return _parts.ContainsKey(ContentTypesPart); }
		}

		/// <summary>
		/// Reads a package fully into memory. The source stream is not retained.
		/// </summary>
		public static SheetStatus TryOpen(Stream source, out OpcPackage? package)
		{
			package = null;

			if (source is null)
			{
				return SheetStatus.ErrNull;
			}
            if (!source.CanRead) return SheetStatus.ErrState;

			OpcPackage result = new OpcPackage();

			try
			{
				using (MemoryStream buffer = new MemoryStream())
				{
					CopyBounded(source, buffer, MaxArchiveBytes);
					buffer.Position = 0;

					using (ZipArchive archive = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true))
					{
						if (archive.Entries.Count > MaxEntries) throw new InvalidDataException("OPC entry limit");
                        long expanded = 0;
                        foreach (ZipArchiveEntry entry in archive.Entries)
						{
							string name = entry.FullName;

							if (name.Length == 0 || name.EndsWith("/", StringComparison.Ordinal))
							{
								result._droppedEntries.Add(name);
								continue;
							}

							if (!SafeName(name)) throw new InvalidDataException("OPC path");
                            expanded += entry.Length;
                            if (entry.Length > MaxPartBytes || expanded > MaxExpandedBytes ||
                                entry.Length > Math.Max(1, entry.CompressedLength) * 2000)
                                throw new InvalidDataException("OPC expansion limit");
                            if (result._parts.ContainsKey(name))
							{
								result._droppedEntries.Add(name);
								continue;
							}

							byte[] bytes;
							using (Stream entryStream = entry.Open())
							using (MemoryStream partBuffer = new MemoryStream())
							{
								CopyBounded(entryStream, partBuffer, Math.Min(MaxPartBytes, entry.Length));
								if (partBuffer.Length != entry.Length) throw new InvalidDataException("OPC member size mismatch");
								bytes = partBuffer.ToArray();
							}

							result._parts.Add(name, bytes);
							result._order.Add(name);
						}
					}
				}
			}
			catch (InvalidDataException)
			{
				result.Dispose();
				return SheetStatus.ErrFormat;
			}
			catch (IOException)
			{
				result.Dispose();
				return SheetStatus.ErrFormat;
			}

			package = result;
			return SheetStatus.Ok;
		}

		/// <summary>
		/// Opens a part for reading. The stream is read-only, seekable and
		/// independent; the caller disposes it. No copy is made.
		/// </summary>
		public SheetStatus TryOpenPart(string partName, out Stream? content)
		{
			content = null;

			if (_disposed)
			{
				return SheetStatus.ErrRange;
			}

			if (partName is null)
			{
				return SheetStatus.ErrNull;
			}

			byte[]? bytes;
			if (!_parts.TryGetValue(partName, out bytes))
			{
				return SheetStatus.ErrMissing;
			}

			content = new MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: false);
			return SheetStatus.Ok;
		}

		/// <summary>Byte length of a part, without opening it.</summary>
		public SheetStatus TryGetPartLength(string partName, out int length)
		{
			length = 0;

			if (partName is null)
			{
				return SheetStatus.ErrNull;
			}

			byte[]? bytes;
			if (!_parts.TryGetValue(partName, out bytes))
			{
				return SheetStatus.ErrMissing;
			}

			length = bytes.Length;
			return SheetStatus.Ok;
		}

		/// <summary>
		/// Adds a new non-empty part at the end of the package. Existing parts
		/// are never replaced implicitly; callers must use TryReplacePart when
		/// changing a known part.
		/// </summary>
		public SheetStatus TryAddPart(string partName, byte[] content)
		{
			if (_disposed) return SheetStatus.ErrRange;
			if (partName is null || content is null) return SheetStatus.ErrNull;
			if (!SafeName(partName) || content.Length == 0 || content.Length > MaxPartBytes) return SheetStatus.ErrFormat;
			if (_parts.ContainsKey(partName)) return SheetStatus.ErrState;
            if (_parts.Count >= MaxEntries || TotalBytes() + content.Length > MaxExpandedBytes) return SheetStatus.ErrLimit;

			_parts.Add(partName, (byte[])content.Clone());
			_order.Add(partName);
			return SheetStatus.Ok;
		}

		/// <summary>
		/// Replaces one part's bytes, keeping its position. A caller that
		/// understands a specific part rewrites that part and nothing else.
		/// </summary>
		public SheetStatus TryReplacePart(string partName, byte[] content)
		{
			if (_disposed)
			{
				return SheetStatus.ErrRange;
			}

			if (partName is null || content is null)
			{
				return SheetStatus.ErrNull;
			}

			if (!_parts.ContainsKey(partName))
			{
				return SheetStatus.ErrMissing;
			}

			if (content.Length > MaxPartBytes || TotalBytes() - _parts[partName].Length + content.Length > MaxExpandedBytes) return SheetStatus.ErrLimit;

            /* The package owns its part bytes. Retaining the caller's array would
			   let later external mutation change the package without going through
			   this method, contradicting the single-mutation boundary above. */
			_parts[partName] = (byte[])content.Clone();
			return SheetStatus.Ok;
		}

		/// <summary>
		/// Writes every part back in its original order. A part with no bytes
		/// fails the save instead of producing a package that looks intact.
		/// </summary>
		public SheetStatus TrySave(Stream destination)
		{
			if (_disposed)
			{
				return SheetStatus.ErrRange;
			}

			if (destination is null)
			{
				return SheetStatus.ErrNull;
			}

			try
			{
				using (ZipArchive archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true))
				{
					for (int i = 0; i < _order.Count; i++)
					{
						string name = _order[i];

						byte[]? bytes;
						if (!_parts.TryGetValue(name, out bytes))
						{
							return SheetStatus.ErrMissing;
						}
						if (bytes.Length == 0)
						{
							return SheetStatus.ErrFormat;
						}

						ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
						using (Stream entryStream = entry.Open())
						{
							entryStream.Write(bytes, 0, bytes.Length);
						}
					}
				}
			}
			catch (IOException)
			{
				return SheetStatus.ErrFormat;
			}

			return SheetStatus.Ok;
		}

        private long TotalBytes() { long n = 0; foreach (byte[] bytes in _parts.Values) n += bytes.Length; return n; }

		/// <inheritdoc />
		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			_parts.Clear();
			_order.Clear();
			_droppedEntries.Clear();
			_disposed = true;
		}
	}
}
