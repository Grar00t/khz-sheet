using System;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace KHZ.Sheet.Core;

public static class BoundedXml
{
    public const int MaxBytes = 16 * 1024 * 1024;
    public static XDocument Load(Stream stream)
    {
        using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > MaxBytes) throw new InvalidDataException("XML byte limit");
            buffer.Write(chunk, 0, read);
        }
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null, MaxCharactersInDocument = MaxBytes, CloseInput = false };
        buffer.Position = 0;
        using (XmlReader reader = XmlReader.Create(buffer, settings))
        {
            int elements = 0;
            while (reader.Read())
            {
                if (reader.Depth > 64 || (reader.NodeType == XmlNodeType.Element && ++elements > 200000))
                    throw new InvalidDataException("XML structural limit");
            }
        }
        buffer.Position = 0;
        using XmlReader validated = XmlReader.Create(buffer, settings);
        return XDocument.Load(validated);
    }
    public static XDocument Load(OpcPackage package, string path)
    {
        if (package.TryOpenPart(path, out Stream? stream) != SheetStatus.Ok || stream is null)
            throw new InvalidDataException("Missing part: " + path);
        using (stream) return Load(stream);
    }
    public static byte[] Bytes(XDocument document)
    {
        using MemoryStream buffer = new();
        using (XmlWriter writer = XmlWriter.Create(buffer, new XmlWriterSettings {
            Encoding = new UTF8Encoding(false), NewLineHandling = NewLineHandling.None })) document.Save(writer);
        if (buffer.Length > MaxBytes) throw new InvalidDataException("XML byte limit");
        return buffer.ToArray();
    }
    public static void Put(OpcPackage package, string path, XDocument document)
    {
        byte[] bytes = Bytes(document);
        SheetStatus status = package.TryGetPartLength(path, out _) == SheetStatus.Ok
            ? package.TryReplacePart(path, bytes) : package.TryAddPart(path, bytes);
        if (status != SheetStatus.Ok) throw new InvalidDataException("Cannot write " + path + ": " + status);
    }
}
