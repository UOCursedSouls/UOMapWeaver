using System;
using System.IO;
using UOMapWeaver.Core.Map;

namespace UOMapWeaver.Tests;

public sealed class TerrainTransitionCatalogTests : IDisposable
{
    private readonly string _tempDir;

    public TerrainTransitionCatalogTests()
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
    public void Load_ValidTerrainAndTransitions_ParsesCorrectly()
    {
        var terrainXml = """
            <?xml version="1.0"?>
            <Terrains>
              <Terrain ID="1" TileID="100" R="0" G="128" B="0" Random="false" />
              <Terrain ID="2" TileID="200" R="0" G="0" B="128" Random="true" />
            </Terrains>
            """;
        var terrainPath = Path.Combine(_tempDir, "Terrain.xml");
        File.WriteAllText(terrainPath, terrainXml);

        var transitionsRoot = Path.Combine(_tempDir, "Transitions");
        Directory.CreateDirectory(transitionsRoot);

        var transitionXml = """
            <?xml version="1.0"?>
            <Transitions>
              <TransInfo HashKey="010101010101010101">
                <MapTile TileID="150" AltIDMod="0" />
                <StaticTile TileID="500" AltIDMod="-2" />
              </TransInfo>
            </Transitions>
            """;
        File.WriteAllText(Path.Combine(transitionsRoot, "test.xml"), transitionXml);

        var catalog = TerrainTransitionCatalog.Load(terrainPath, transitionsRoot, null);

        // Terrain definitions
        Assert.Equal(2, catalog.TerrainById.Count);
        Assert.Equal((ushort)100, catalog.TerrainById[1].TileId);
        Assert.Equal((ushort)200, catalog.TerrainById[2].TileId);
        Assert.False(catalog.TerrainById[1].Random);
        Assert.True(catalog.TerrainById[2].Random);

        // Color mapping
        Assert.Equal(2, catalog.TerrainIdByColor.Count);
        var greenKey = (0 << 16) | (128 << 8) | 0;
        Assert.Equal(1, catalog.TerrainIdByColor[greenKey]);

        // Transitions
        Assert.Single(catalog.Transitions);
        Assert.True(catalog.Transitions.ContainsKey("010101010101010101"));
        var set = catalog.Transitions["010101010101010101"];
        Assert.Single(set.MapTiles);
        Assert.Equal((ushort)150, set.MapTiles[0].TileId);
        Assert.Single(set.StaticTiles);
        Assert.Equal((ushort)500, set.StaticTiles[0].TileId);
        Assert.Equal((sbyte)-2, set.StaticTiles[0].AltitudeMod);

        // Base tiles (solid hash = all same 2-char segments)
        Assert.Single(catalog.BaseTiles);
        Assert.True(catalog.BaseTiles.ContainsKey(1));
    }

    [Fact]
    public void Load_MissingTerrainXml_ReturnsEmptyCatalog()
    {
        var transitionsRoot = Path.Combine(_tempDir, "EmptyTransitions");
        Directory.CreateDirectory(transitionsRoot);

        var logged = new List<MapConversionLogEntry>();
        var catalog = TerrainTransitionCatalog.Load(
            Path.Combine(_tempDir, "nonexistent.xml"),
            transitionsRoot,
            entry => logged.Add(entry));

        Assert.Empty(catalog.TerrainById);
        Assert.Empty(catalog.TerrainIdByColor);
        Assert.Contains(logged, e => e.Level == MapConversionLogLevel.Warning);
    }

    [Fact]
    public void Load_MissingTransitionsFolder_ReturnsEmptyTransitions()
    {
        var terrainXml = """
            <?xml version="1.0"?>
            <Terrains>
              <Terrain ID="1" TileID="100" R="0" G="128" B="0" Random="false" />
            </Terrains>
            """;
        var terrainPath = Path.Combine(_tempDir, "Terrain2.xml");
        File.WriteAllText(terrainPath, terrainXml);

        var logged = new List<MapConversionLogEntry>();
        var catalog = TerrainTransitionCatalog.Load(
            terrainPath,
            Path.Combine(_tempDir, "NoSuchFolder"),
            entry => logged.Add(entry));

        Assert.Single(catalog.TerrainById);
        Assert.Empty(catalog.Transitions);
        Assert.Contains(logged, e => e.Level == MapConversionLogLevel.Warning);
    }

    [Fact]
    public void Load_MultipleTransitionFiles_MergesCorrectly()
    {
        var terrainXml = """
            <?xml version="1.0"?>
            <Terrains>
              <Terrain ID="1" TileID="100" R="10" G="20" B="30" Random="false" />
            </Terrains>
            """;
        var terrainPath = Path.Combine(_tempDir, "TerrainMerge.xml");
        File.WriteAllText(terrainPath, terrainXml);

        var transitionsRoot = Path.Combine(_tempDir, "TransitionsMerge");
        Directory.CreateDirectory(transitionsRoot);

        var xml1 = """
            <?xml version="1.0"?>
            <Transitions>
              <TransInfo HashKey="AABBCCDDEE112233FF">
                <MapTile TileID="300" AltIDMod="1" />
              </TransInfo>
            </Transitions>
            """;
        File.WriteAllText(Path.Combine(transitionsRoot, "file1.xml"), xml1);

        var xml2 = """
            <?xml version="1.0"?>
            <Transitions>
              <TransInfo HashKey="AABBCCDDEE112233FF">
                <MapTile TileID="301" AltIDMod="-1" />
              </TransInfo>
            </Transitions>
            """;
        File.WriteAllText(Path.Combine(transitionsRoot, "file2.xml"), xml2);

        var catalog = TerrainTransitionCatalog.Load(terrainPath, transitionsRoot, null);

        Assert.Single(catalog.Transitions);
        var set = catalog.Transitions["AABBCCDDEE112233FF"];
        Assert.Equal(2, set.MapTiles.Count);
    }

    [Fact]
    public void Load_NonSolidHash_NotAddedToBaseTiles()
    {
        var terrainXml = """
            <?xml version="1.0"?>
            <Terrains>
              <Terrain ID="1" TileID="100" R="0" G="0" B="1" Random="false" />
            </Terrains>
            """;
        var terrainPath = Path.Combine(_tempDir, "TerrainNonSolid.xml");
        File.WriteAllText(terrainPath, terrainXml);

        var transitionsRoot = Path.Combine(_tempDir, "TransitionsNonSolid");
        Directory.CreateDirectory(transitionsRoot);

        // Non-solid hash (different segments)
        var xml = """
            <?xml version="1.0"?>
            <Transitions>
              <TransInfo HashKey="010203040506070809">
                <MapTile TileID="400" AltIDMod="0" />
              </TransInfo>
            </Transitions>
            """;
        File.WriteAllText(Path.Combine(transitionsRoot, "nonsol.xml"), xml);

        var catalog = TerrainTransitionCatalog.Load(terrainPath, transitionsRoot, null);

        Assert.Empty(catalog.BaseTiles);
        Assert.Single(catalog.Transitions);
    }
}
