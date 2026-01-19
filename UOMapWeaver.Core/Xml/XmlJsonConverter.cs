using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using System.Text.Json.Serialization;

namespace UOMapWeaver.Core.Xml;

public static class XmlJsonConverter
{
    public static XmlJsonDocument Convert(string xml)
    {
        var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        if (doc.Root is null)
        {
            throw new InvalidOperationException("XML document has no root element.");
        }

        var root = ConvertNode(doc.Root);
        return new XmlJsonDocument(root);
    }

    public static string ConvertToJson(string xml)
    {
        var doc = Convert(xml);
        return JsonSerializer.Serialize(doc, CreateOptions());
    }

    private static XmlJsonNode ConvertNode(XNode node)
    {
        return node switch
        {
            XElement element => ConvertElement(element),
            XCData cdata => new XmlJsonNode("cdata", null, cdata.Value, null, null),
            XText text => new XmlJsonNode("text", null, text.Value, null, null),
            XComment comment => new XmlJsonNode("comment", null, comment.Value, null, null),
            XProcessingInstruction pi => new XmlJsonNode("processingInstruction", pi.Target, pi.Data, null, null),
            XDocumentType docType => new XmlJsonNode("documentType", docType.Name, docType.ToString(), null, null),
            _ => new XmlJsonNode("unknown", null, node.ToString(), null, null)
        };
    }

    private static XmlJsonNode ConvertElement(XElement element)
    {
        var attributes = element.Attributes()
            .ToDictionary(attr => attr.Name.LocalName, attr => attr.Value, StringComparer.OrdinalIgnoreCase);

        if (attributes.Count == 0)
        {
            attributes = null;
        }

        var children = element.Nodes()
            .Select(ConvertNode)
            .ToList();

        if (children.Count == 0)
        {
            children = null;
        }

        return new XmlJsonNode(null, element.Name.LocalName, null, attributes, children);
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

public sealed record XmlJsonDocument(XmlJsonNode Root);

public sealed class XmlJsonNode
{
    public XmlJsonNode(
        string? kind,
        string? name,
        string? value,
        Dictionary<string, string>? attributes,
        List<XmlJsonNode>? children)
    {
        Kind = kind;
        Name = name;
        Value = value;
        Attributes = attributes;
        Children = children;
    }

    public string? Kind { get; }

    public string? Name { get; }
    public string? Value { get; }
    public Dictionary<string, string>? Attributes { get; }
    public List<XmlJsonNode>? Children { get; }
}
