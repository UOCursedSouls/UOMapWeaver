// SPDX-License-Identifier: GPL-3.0-only
using UOMapWeaver.Core.WorldFormats;
using UOMapWeaver.Plugins;

namespace UOMapWeaver.Tests;

public sealed class WorldAreaEditingTests
{
    private sealed class MemoryPlugin(WorldFormatCapabilities caps) : IWorldFormatPlugin, IWorldAreaPlugin
    {
        public List<WorldArea> Saved = [new WorldArea("0", "Town", "TownRegion", null, [new WorldAreaRect(10, 20, 30, 40, 0, 5)])];
        public int WriteAllCalls;
        public int ContractVersion => WorldFormatContract.Version;
        public string Id => "memory";
        public string DisplayName => "Memory";
        public WorldFormatCapabilities Capabilities => caps;
        public IWorldWriter CreateWorld(string worldFolder, WorldCreateOptions options) => throw new NotSupportedException();
        public IWorldReader OpenWorld(string worldFolder) => throw new NotSupportedException();
        public IWorldAreaStore OpenAreas(string worldFolder) => new Store(this);

        private sealed class Store(MemoryPlugin p) : IWorldAreaStore
        {
            public IReadOnlyList<string> Kinds => ["TownRegion", "GuardedRegion"];
            public IReadOnlyList<WorldArea> ReadAll() => p.Saved.ToList();
            public void WriteAll(IReadOnlyList<WorldArea> areas)
            {
                p.WriteAllCalls++;
                p.Saved = areas.Select((a, i) => a with { Id = a.Id ?? "n" + i }).ToList();
            }

            public void Dispose() { }
        }
    }

    [Fact]
    public void WorldAreaEditing_CreateMoveRenameDeleteSave_ReachesWriteAll()
    {
        var plugin = new MemoryPlugin(WorldFormatCapabilities.Areas);
        using var editor = WorldAreaEditor.Open(plugin, "w")!;

        var created = editor.Create("GuardedRegion", "Keep", new WorldAreaRect(0, 0, 5, 5, -128, 127));
        editor.SetRect(created, 0, new WorldAreaRect(100, 200, 5, 5, -128, 127));
        editor.Rename(created, "Castle");
        editor.Create("TownRegion", "Temp", new WorldAreaRect(1, 1, 2, 2, 0, 0));
        editor.Delete(1 + created);
        Assert.True(editor.Dirty);
        Assert.Equal(0, plugin.WriteAllCalls);

        editor.Save();

        Assert.Equal(1, plugin.WriteAllCalls);
        Assert.Equal(["Town", "Castle"], plugin.Saved.Select(a => a.Name));
        Assert.Equal(new WorldAreaRect(100, 200, 5, 5, -128, 127), plugin.Saved[1].Rects.Single());
        Assert.Equal("GuardedRegion", plugin.Saved[1].Kind);
        Assert.False(editor.Dirty);
    }

    [Fact]
    public void WorldAreaEditing_DeleteParent_ChildrenMoveUp()
    {
        var plugin = new MemoryPlugin(WorldFormatCapabilities.Areas);
        plugin.Saved.Add(new WorldArea("1", "Inn", "TownRegion", "0", [new WorldAreaRect(11, 21, 2, 2, 0, 5)]));
        using var editor = WorldAreaEditor.Open(plugin, "w")!;
        editor.Delete(0);
        editor.Save();
        Assert.Null(Assert.Single(plugin.Saved).ParentId);
    }

    [Fact]
    public void WorldAreaEditing_UnknownKindOrEmptyRect_Rejected()
    {
        using var editor = WorldAreaEditor.Open(new MemoryPlugin(WorldFormatCapabilities.Areas), "w")!;
        Assert.Throws<ArgumentException>(() => editor.Create("Nope", "x", new WorldAreaRect(0, 0, 1, 1, 0, 0)));
        Assert.Throws<ArgumentException>(() => editor.Create("TownRegion", "x", new WorldAreaRect(0, 0, 0, 1, 0, 0)));
    }

    [Fact]
    public void WorldAreaEditing_WithoutCapability_HidesAreaTools() =>
        Assert.Null(WorldAreaEditor.Open(new MemoryPlugin(WorldFormatCapabilities.Import | WorldFormatCapabilities.Export), "w"));
}
