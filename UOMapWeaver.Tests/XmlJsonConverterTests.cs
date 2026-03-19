using System.Text.Json;
using UOMapWeaver.Core.Xml;
using UOMapWeaver.Core.TileColors;

namespace UOMapWeaver.Tests;

public sealed class XmlJsonConverterTests
{
    [Fact]
    public void Convert_SimpleElement_ParsesCorrectly()
    {
        var xml = "<Root><Item Name=\"Test\" Value=\"42\" /></Root>";
        var doc = XmlJsonConverter.Convert(xml);

        Assert.NotNull(doc.Root);
        Assert.Equal("Root", doc.Root.Name);
        Assert.NotNull(doc.Root.Children);
        Assert.Single(doc.Root.Children);

        var item = doc.Root.Children[0];
        Assert.Equal("Item", item.Name);
        Assert.NotNull(item.Attributes);
        Assert.Equal("Test", item.Attributes["Name"]);
        Assert.Equal("42", item.Attributes["Value"]);
    }

    [Fact]
    public void Convert_NestedElements_PreservesHierarchy()
    {
        var xml = "<A><B><C Id=\"1\" /></B></A>";
        var doc = XmlJsonConverter.Convert(xml);

        Assert.Equal("A", doc.Root.Name);
        Assert.NotNull(doc.Root.Children);
        Assert.Single(doc.Root.Children);
        var b = doc.Root.Children[0];
        Assert.Equal("B", b.Name);
        Assert.NotNull(b.Children);
        Assert.Single(b.Children);
        var c = b.Children[0];
        Assert.Equal("C", c.Name);
        Assert.NotNull(c.Attributes);
        Assert.Equal("1", c.Attributes["Id"]);
    }

    [Fact]
    public void ConvertToJson_ProducesValidJson()
    {
        var xml = "<Root><Item Name=\"Test\" /></Root>";
        var json = XmlJsonConverter.ConvertToJson(xml);

        Assert.False(string.IsNullOrWhiteSpace(json));
        // Should be valid JSON
        var doc = JsonDocument.Parse(json);
        Assert.NotNull(doc);
    }

    [Fact]
    public void Convert_EmptyElement_HasNoChildren()
    {
        var xml = "<Root></Root>";
        var doc = XmlJsonConverter.Convert(xml);

        Assert.Equal("Root", doc.Root.Name);
        Assert.Null(doc.Root.Children);
        Assert.Null(doc.Root.Attributes);
    }

    [Fact]
    public void Convert_TextContent_CapturedAsTextNode()
    {
        var xml = "<Root>Hello World</Root>";
        var doc = XmlJsonConverter.Convert(xml);

        Assert.NotNull(doc.Root.Children);
        Assert.Single(doc.Root.Children);
        Assert.Equal("text", doc.Root.Children[0].Kind);
        Assert.Equal("Hello World", doc.Root.Children[0].Value);
    }
}

public sealed class RgbColorTests
{
    [Fact]
    public void Constructor_StoresComponents()
    {
        var color = new RgbColor(10, 20, 30);
        Assert.Equal((byte)10, color.R);
        Assert.Equal((byte)20, color.G);
        Assert.Equal((byte)30, color.B);
    }

    [Fact]
    public void Key_EncodesCorrectly()
    {
        var color = new RgbColor(0xFF, 0x80, 0x40);
        Assert.Equal((0xFF << 16) | (0x80 << 8) | 0x40, color.Key);
    }

    [Fact]
    public void ToHex_FormatsCorrectly()
    {
        var color = new RgbColor(0, 128, 255);
        Assert.Equal("#0080FF", color.ToHex());
    }

    [Fact]
    public void TryParse_ValidHex_Succeeds()
    {
        Assert.True(RgbColor.TryParse("#FF8040", out var color));
        Assert.Equal((byte)0xFF, color.R);
        Assert.Equal((byte)0x80, color.G);
        Assert.Equal((byte)0x40, color.B);
    }

    [Fact]
    public void TryParse_WithoutHash_Succeeds()
    {
        Assert.True(RgbColor.TryParse("AABBCC", out var color));
        Assert.Equal((byte)0xAA, color.R);
        Assert.Equal((byte)0xBB, color.G);
        Assert.Equal((byte)0xCC, color.B);
    }

    [Fact]
    public void TryParse_InvalidInput_ReturnsFalse()
    {
        Assert.False(RgbColor.TryParse("", out _));
        Assert.False(RgbColor.TryParse("ABC", out _));
        Assert.False(RgbColor.TryParse("ZZZZZZ", out _));
        Assert.False(RgbColor.TryParse(null!, out _));
    }

    [Fact]
    public void RoundTrip_ToHexThenParse()
    {
        var original = new RgbColor(42, 99, 200);
        Assert.True(RgbColor.TryParse(original.ToHex(), out var parsed));
        Assert.Equal(original.R, parsed.R);
        Assert.Equal(original.G, parsed.G);
        Assert.Equal(original.B, parsed.B);
    }
}
