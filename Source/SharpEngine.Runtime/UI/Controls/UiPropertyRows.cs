using System.Globalization;
using System.Text;
using System.Xml;

namespace SharpEngine.UI;

// Nest names within a shared column; containers never inset their value column.
internal static class UiPropertyRows
{
    internal static string Indent(string Markup)
    {
        if (Markup.Length == 0)
        {
            return Markup;
        }
        var Output = new StringBuilder(Markup.Length);
        using var Reader = XmlReader.Create(new StringReader(Markup), new XmlReaderSettings
        {
            ConformanceLevel = ConformanceLevel.Fragment,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreWhitespace = true,
        });
        using var Writer = XmlWriter.Create(Output, new XmlWriterSettings
        {
            ConformanceLevel = ConformanceLevel.Fragment,
            OmitXmlDeclaration = true,
            NewLineHandling = NewLineHandling.Entitize,
        });
        Reader.Read();
        while (!Reader.EOF)
        {
            if (Reader.NodeType == XmlNodeType.Element)
            {
                var IsName = Reader.GetAttribute("class")?.Split(' ').Contains("se-property-name") == true;
                var Empty = Reader.IsEmptyElement;
                Writer.WriteStartElement(Reader.Prefix, Reader.LocalName, Reader.NamespaceURI);
                if (IsName)
                {
                    var Depth = int.Parse(Reader.GetAttribute("data-property-depth") ?? "0", CultureInfo.InvariantCulture) + 1;
                    while (Reader.MoveToNextAttribute())
                    {
                        if (Reader.Name is not "data-property-depth" and not "style")
                        {
                            Writer.WriteAttributeString(Reader.Prefix, Reader.LocalName, Reader.NamespaceURI, Reader.Value);
                        }
                    }
                    Reader.MoveToElement();
                    Writer.WriteAttributeString("data-property-depth", Depth.ToString(CultureInfo.InvariantCulture));
                    Writer.WriteAttributeString("style", "padding-left:" + (8 + Depth * 12).ToString(CultureInfo.InvariantCulture) + "px;");
                }
                else
                {
                    Writer.WriteAttributes(Reader, false);
                }
                if (Empty)
                {
                    Writer.WriteEndElement();
                }
                Reader.Read();
                continue;
            }
            if (Reader.NodeType == XmlNodeType.EndElement)
            {
                Writer.WriteFullEndElement();
                Reader.Read();
                continue;
            }
            Writer.WriteNode(Reader, false);
        }
        Writer.Flush();
        return Output.ToString();
    }
}
