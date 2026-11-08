// SPDX-License-Identifier: GPL-3.0-only
using UOMapWeaver.Core.Plugins;
using UOMapWeaver.Plugins;

namespace UOMapWeaver.Tests;

public sealed class WorldAreaPluginTests
{
    private sealed class MemoryPlugin(WorldFormatCapabilities caps) : IWorldFormatPlugin, IWorldAreaPlugin
    {
        public readonly List<WorldArea> Saved = [];
        public int ContractVersion => WorldFormatContract.Version;
        public string Id => "memory";
        public string DisplayName => "Memory";
        public WorldFormatCapabilities Capabilities => caps;
        public IWorldWriter CreateWorld(string worldFolder, WorldCreateOptions options) => throw new NotSupportedException();
        public IWorldReader OpenWorld(string worldFolder) => throw new NotSupportedException();
        public IWorldAreaStore OpenAreas(string worldFolder) => new Store(Saved);

        private sealed class Store(List<WorldArea> saved) : IWorldAreaStore
        {
            public IReadOnlyList<string> Kinds => ["TownRegion"];
            public IReadOnlyList<WorldArea> ReadAll() => saved.ToList();
            public void WriteAll(IReadOnlyList<WorldArea> areas) { saved.Clear(); saved.AddRange(areas); }
            public void Dispose() { }
        }
    }

    [Fact]
    public void WorldAreaPlugin_ReadAllWriteAll_RoundTrips()
    {
        var plugin = new MemoryPlugin(WorldFormatCapabilities.Import | WorldFormatCapabilities.Areas);
        var areas = new[]
        {
            new WorldArea("0", "Town", "TownRegion", null, [new WorldAreaRect(10, 20, 30, 40, -128, 127)]),
            new WorldArea(null, "Inn", "TownRegion", "0", [new WorldAreaRect(1, 2, 3, 4, 0, 5)]),
        };

        var store = PluginLoader.GetAreaPlugin(plugin)!.OpenAreas("w");
        store.WriteAll(areas);

        var read = store.ReadAll();
        Assert.Equal(areas.Select(a => (a.Id, a.Name, a.Kind, a.ParentId)), read.Select(a => (a.Id, a.Name, a.Kind, a.ParentId)));
        Assert.Equal(areas.SelectMany(a => a.Rects), read.SelectMany(a => a.Rects));
        Assert.Contains("TownRegion", store.Kinds);
    }

    [Fact]
    public void WorldAreaPlugin_WithoutCapability_OffersNoAreas() =>
        Assert.Null(PluginLoader.GetAreaPlugin(new MemoryPlugin(WorldFormatCapabilities.Import | WorldFormatCapabilities.Export)));

    [Fact]
    public void WorldAreaPlugin_PluginWithoutInterface_OffersNoAreas() =>
        Assert.Null(PluginLoader.GetAreaPlugin(new NoAreaPlugin()));

    [Fact]
    public void WorldAreaPlugin_Contract1Plugin_RefusedAsIncompatible()
    {
        var root = Path.Combine(Path.GetTempPath(), "uomw-area-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            PluginLoaderTests.WritePlugin(root, "Old", "old", 1);
            var log = new List<string>();
            Assert.Empty(PluginLoader.Load([root], log.Add));
            Assert.Contains(log, m => m.Contains("incompatible (contract v1, needs v2)"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void WorldAreaPlugin_LoadedPluginWithoutAreas_IsLoadedButOffersNone()
    {
        var root = Path.Combine(Path.GetTempPath(), "uomw-area-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            PluginLoaderTests.WritePlugin(root, "Plain", "plain", WorldFormatContract.Version);
            var p = Assert.Single(PluginLoader.Load([root]));
            Assert.Null(PluginLoader.GetAreaPlugin(p));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class NoAreaPlugin : IWorldFormatPlugin
    {
        public int ContractVersion => WorldFormatContract.Version;
        public string Id => "x";
        public string DisplayName => "x";
        public WorldFormatCapabilities Capabilities => WorldFormatCapabilities.Areas;
        public IWorldWriter CreateWorld(string worldFolder, WorldCreateOptions options) => throw new NotSupportedException();
        public IWorldReader OpenWorld(string worldFolder) => throw new NotSupportedException();
    }
}
