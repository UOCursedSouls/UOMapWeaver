using System;
using System.IO;
using UOMapWeaver.Core.MapTrans;

namespace UOMapWeaver.Tests;

public sealed class MapTransSerializerTests : IDisposable
{
    private readonly string _tempDir;

    public MapTransSerializerTests()
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
    public void SaveLoad_RoundTrip_PreservesEntries()
    {
        var entries = new List<MapTransEntry>
        {
            new(10, -5, new ushort[] { 0x0001, 0x0002, 0x0003 }, group: 1),
            new(20, 42, new ushort[] { 0x0100 }),
            new(30, 0, new ushort[] { 0x2000, 0x2001 }, group: null)
        };

        var profile = new MapTransProfile("TestProfile", entries, null);
        var path = Path.Combine(_tempDir, "test_maptrans.json");

        MapTransJsonSerializer.Save(path, profile);
        Assert.True(File.Exists(path));

        var loaded = MapTransJsonSerializer.Load(path);

        Assert.Equal("TestProfile", loaded.Name);
        Assert.Equal(3, loaded.Entries.Count);

        Assert.Equal((byte)10, loaded.Entries[0].ColorIndex);
        Assert.Equal(-5, loaded.Entries[0].Altitude);
        Assert.Equal(3, loaded.Entries[0].TileIds.Count);
        Assert.Equal((ushort)0x0001, loaded.Entries[0].TileIds[0]);
        Assert.Equal((ushort)0x0002, loaded.Entries[0].TileIds[1]);
        Assert.Equal((ushort)0x0003, loaded.Entries[0].TileIds[2]);
        Assert.Equal((byte)1, loaded.Entries[0].Group);

        Assert.Equal((byte)20, loaded.Entries[1].ColorIndex);
        Assert.Equal(42, loaded.Entries[1].Altitude);
        Assert.Single(loaded.Entries[1].TileIds);
        Assert.Null(loaded.Entries[1].Group);

        Assert.Equal((byte)30, loaded.Entries[2].ColorIndex);
        Assert.Equal(0, loaded.Entries[2].Altitude);
        Assert.Equal(2, loaded.Entries[2].TileIds.Count);
        Assert.Null(loaded.Entries[2].Group);
    }

    [Fact]
    public void SaveLoad_EmptyProfile_RoundTrips()
    {
        var profile = new MapTransProfile("Empty", new List<MapTransEntry>(), null);
        var path = Path.Combine(_tempDir, "empty_maptrans.json");

        MapTransJsonSerializer.Save(path, profile);
        var loaded = MapTransJsonSerializer.Load(path);

        Assert.Equal("Empty", loaded.Name);
        Assert.Empty(loaded.Entries);
    }

    [Fact]
    public void Load_MissingFile_ThrowsFileNotFoundException()
    {
        Assert.Throws<FileNotFoundException>(() =>
            MapTransJsonSerializer.Load(Path.Combine(_tempDir, "nonexistent.json")));
    }

    [Fact]
    public void Profile_EntriesByColor_GroupsCorrectly()
    {
        var entries = new List<MapTransEntry>
        {
            new(5, 0, new ushort[] { 100 }),
            new(5, 10, new ushort[] { 200 }),
            new(10, -5, new ushort[] { 300 })
        };

        var profile = new MapTransProfile("Grouped", entries, null);

        Assert.Equal(2, profile.EntriesByColor.Count);
        Assert.Equal(2, profile.EntriesByColor[5].Count);
        Assert.Single(profile.EntriesByColor[10]);
    }
}
