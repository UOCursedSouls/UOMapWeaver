using System;
using System.IO;
using UOMapWeaver.Core.Map;

namespace UOMapWeaver.Tests;

public sealed class AltitudeCatalogTests : IDisposable
{
    private readonly string _tempDir;

    public AltitudeCatalogTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "UOMapWeaver.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Fact]
    public void Load_ValidXml_ParsesAltitudes()
    {
        var xml = """
            <?xml version="1.0"?>
            <Altitudes>
              <Altitude R="255" G="0" B="0" Altitude="10" />
              <Altitude R="0" G="255" B="0" Altitude="-5" />
              <Altitude R="0" G="0" B="255" Altitude="127" />
              <Altitude R="128" G="128" B="128" Altitude="-128" />
            </Altitudes>
            """;
        var path = Path.Combine(_tempDir, "Altitude.xml");
        File.WriteAllText(path, xml);

        var catalog = AltitudeCatalog.Load(path, null);

        Assert.Equal(4, catalog.AltitudeByColor.Count);

        // Red = (255 << 16) | (0 << 8) | 0 = 16711680
        Assert.Equal((sbyte)10, catalog.AltitudeByColor[(255 << 16) | (0 << 8) | 0]);
        // Green
        Assert.Equal((sbyte)-5, catalog.AltitudeByColor[(0 << 16) | (255 << 8) | 0]);
        // Blue
        Assert.Equal((sbyte)127, catalog.AltitudeByColor[(0 << 16) | (0 << 8) | 255]);
        // Gray
        Assert.Equal((sbyte)-128, catalog.AltitudeByColor[(128 << 16) | (128 << 8) | 128]);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyCatalog()
    {
        var path = Path.Combine(_tempDir, "nonexistent.xml");
        var logged = new List<MapConversionLogEntry>();

        var catalog = AltitudeCatalog.Load(path, entry => logged.Add(entry));

        Assert.Empty(catalog.AltitudeByColor);
        Assert.Contains(logged, e => e.Level == MapConversionLogLevel.Warning);
    }

    [Fact]
    public void Load_EmptyXml_ReturnsEmptyCatalog()
    {
        var xml = """
            <?xml version="1.0"?>
            <Altitudes>
            </Altitudes>
            """;
        var path = Path.Combine(_tempDir, "Empty.xml");
        File.WriteAllText(path, xml);

        var catalog = AltitudeCatalog.Load(path, null);

        Assert.Empty(catalog.AltitudeByColor);
    }

    [Fact]
    public void Load_AltitudeClampedToSbyteRange()
    {
        var xml = """
            <?xml version="1.0"?>
            <Altitudes>
              <Altitude R="1" G="0" B="0" Altitude="200" />
              <Altitude R="0" G="1" B="0" Altitude="-200" />
            </Altitudes>
            """;
        var path = Path.Combine(_tempDir, "Clamped.xml");
        File.WriteAllText(path, xml);

        var catalog = AltitudeCatalog.Load(path, null);

        Assert.Equal(2, catalog.AltitudeByColor.Count);
        Assert.Equal((sbyte)127, catalog.AltitudeByColor[(1 << 16) | (0 << 8) | 0]);
        Assert.Equal((sbyte)-128, catalog.AltitudeByColor[(0 << 16) | (1 << 8) | 0]);
    }

    [Fact]
    public void Load_DuplicateColors_LastWins()
    {
        var xml = """
            <?xml version="1.0"?>
            <Altitudes>
              <Altitude R="10" G="20" B="30" Altitude="5" />
              <Altitude R="10" G="20" B="30" Altitude="42" />
            </Altitudes>
            """;
        var path = Path.Combine(_tempDir, "Duplicate.xml");
        File.WriteAllText(path, xml);

        var catalog = AltitudeCatalog.Load(path, null);

        Assert.Single(catalog.AltitudeByColor);
        Assert.Equal((sbyte)42, catalog.AltitudeByColor[(10 << 16) | (20 << 8) | 30]);
    }

    [Fact]
    public void Load_MissingAttributes_DefaultsToZero()
    {
        var xml = """
            <?xml version="1.0"?>
            <Altitudes>
              <Altitude Altitude="50" />
            </Altitudes>
            """;
        var path = Path.Combine(_tempDir, "NoColor.xml");
        File.WriteAllText(path, xml);

        var catalog = AltitudeCatalog.Load(path, null);

        // R=0, G=0, B=0 -> colorKey = 0
        Assert.Single(catalog.AltitudeByColor);
        Assert.Equal((sbyte)50, catalog.AltitudeByColor[0]);
    }
}
