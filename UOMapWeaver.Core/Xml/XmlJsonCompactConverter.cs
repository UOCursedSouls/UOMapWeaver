using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace UOMapWeaver.Core.Xml;

public static class XmlJsonCompactConverter
{
    public static string ConvertToJson(string xml)
    {
        var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        if (doc.Root is null)
        {
            throw new InvalidOperationException("XML document has no root element.");
        }

        var root = ConvertNode(doc.Root);
        var payload = new XmlJsonCompactDocument(root);
        return JsonSerializer.Serialize(payload, CreateOptions());
    }

    private static XmlJsonCompactNode ConvertNode(XNode node)
    {
        return node switch
        {
            XElement element => ConvertElement(element),
            XCData cdata => new XmlJsonCompactNode(null, null, null, null, null, cdata.Value, null, null),
            XText text => new XmlJsonCompactNode(null, null, null, null, null, text.Value, null, null),
            XProcessingInstruction pi => new XmlJsonCompactNode(null, null, null, null, null, null, pi.Target, pi.Data),
            XDocumentType docType => new XmlJsonCompactNode(null, null, null, null, docType.ToString(), null, null, null),
            _ => new XmlJsonCompactNode(null, null, null, null, node.ToString(), null, null, null)
        };
    }

    private static XmlJsonCompactNode ConvertElement(XElement element)
    {
        var attributes = element.Attributes()
            .ToDictionary(attr => attr.Name.LocalName, attr => attr.Value, StringComparer.OrdinalIgnoreCase);
        if (attributes.Count == 0)
        {
            attributes = null;
        }

        var children = new List<XmlJsonCompactNode>();
        foreach (var child in element.Nodes())
        {
            if (child is XText text && string.IsNullOrWhiteSpace(text.Value))
            {
                continue;
            }

            if (child is XComment)
            {
                continue;
            }

            children.Add(ConvertNode(child));
        }

        if (children.Count == 0)
        {
            children = null;
        }

        return new XmlJsonCompactNode(element.Name.LocalName, attributes, children, null, null, null, null, null);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        return new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }
}

public sealed record XmlJsonCompactDocument(XmlJsonCompactNode Root);

public sealed record XmlJsonCompactNode(
    string? Name,
    Dictionary<string, string>? Attributes,
    List<XmlJsonCompactNode>? Children,
    string? DocType,
    string? Unknown,
    string? Text,
    string? ProcessingInstructionTarget,
    string? ProcessingInstructionData);
