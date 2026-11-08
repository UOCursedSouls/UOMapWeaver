// SPDX-License-Identifier: GPL-3.0-only
using UOMapWeaver.Core.TileColors;
using UOMapWeaver.Core.WorldFormats;
using UOMapWeaver.Plugins;

namespace UOMapWeaver.Tests;

public sealed class WorldAreaMapViewTests
{
    private sealed class MemoryPlugin : IWorldFormatPlugin, IWorldAreaPlugin
    {
        public List<WorldArea> Saved =
        [
            new WorldArea("0", "Town", "TownRegion", null, [new WorldAreaRect(10, 20, 30, 40, 0, 5)]),
            new WorldArea("1", "Keep", "GuardedRegion", null, [new WorldAreaRect(100, 100, 8, 8, 0, 5)]),
        ];

        public ManualResetEventSlim InWrite = new();
        public ManualResetEventSlim ReleaseWrite = new();
        public bool BlockWrite;
        public int ContractVersion => WorldFormatContract.Version;
        public string Id => "memory";
        public string DisplayName => "Memory";
        public WorldFormatCapabilities Capabilities => WorldFormatCapabilities.Areas;
        public IWorldWriter CreateWorld(string worldFolder, WorldCreateOptions options) => throw new NotSupportedException();
        public IWorldReader OpenWorld(string worldFolder) => throw new NotSupportedException();
        public IWorldAreaStore OpenAreas(string worldFolder) => new Store(this);

        private sealed class Store(MemoryPlugin p) : IWorldAreaStore
        {
            public IReadOnlyList<string> Kinds => ["TownRegion", "GuardedRegion"];
            public IReadOnlyList<WorldArea> ReadAll() => p.Saved.ToList();

            public void WriteAll(IReadOnlyList<WorldArea> areas)
            {
                if (p.BlockWrite)
                {
                    p.InWrite.Set();
                    p.ReleaseWrite.Wait();
                }

                p.Saved = areas.ToList();
            }

            public void Dispose() { }
        }
    }

    private sealed class CountingReader : IWorldReader
    {
        public List<(int, int)> Reads = [];
        public WorldInfo Info => new("w", 100, 100);

        public bool TryReadBlock(int blockX, int blockY, out WorldBlock? block)
        {
            Reads.Add((blockX, blockY));
            if (blockX == 1)
            {
                block = null;
                return false;
            }

            block = new WorldBlock(blockX, blockY, Enumerable.Repeat(new WorldLandTile(3, 0), 64).ToArray(), []);
            return true;
        }

        public void Dispose() { }
    }

    private static WorldAreaMapView NewView(MemoryPlugin plugin, CountingReader? reader = null)
        => new(WorldAreaEditor.Open(plugin, "w")!, reader, _ => new RgbColor(1, 2, 3));

    [Fact]
    public void WorldAreaMapView_VisibleRange_ReadsOnlyShownBlocksAndCachesThem()
    {
        var reader = new CountingReader();
        using var view = NewView(new MemoryPlugin(), reader);
        view.SetView(0, 0, 1);

        Assert.Equal(3, view.LoadVisible(23, 7)); // tiles 0..23 x 0..7 -> blocks x 0..2, y 0
        Assert.Equal([(0, 0), (1, 0), (2, 0)], reader.Reads);
    }

    [Fact]
    public void WorldAreaMapView_SecondRenderOfSameRange_ReadsNothing()
    {
        var reader = new CountingReader();
        using var view = NewView(new MemoryPlugin(), reader);
        view.SetView(0, 0, 1);

        Assert.Equal(8, view.LoadVisible(31, 15)); // blocks x 0..3, y 0..1
        var first = reader.Reads.Count;
        Assert.Equal(0, view.LoadVisible(31, 15));
        Assert.Equal(first, reader.Reads.Count);
    }

    [Fact]
    public void WorldAreaMapView_MissingBlock_IsCachedEmptyWithoutError()
    {
        var reader = new CountingReader();
        using var view = NewView(new MemoryPlugin(), reader);
        view.SetView(0, 0, 1);
        view.LoadVisible(24, 8);

        Assert.True(view.TryGetCached(1, 0, out var missing));
        Assert.Null(missing);
        Assert.True(view.TryGetCached(0, 0, out var present));
        Assert.Equal(new RgbColor(1, 2, 3).Key, present!.Mean.Key);
    }

    [Fact]
    public void WorldAreaMapView_SelectByListIndex_RenameAndDeleteHitThatArea()
    {
        var plugin = new MemoryPlugin();
        using var view = NewView(plugin);

        view.Select(1);
        view.RenameSelected("Castle");
        Assert.Equal(["Town", "Castle"], view.Editor.Areas.Select(a => a.Name));

        view.DeleteSelected();
        Assert.Equal(["Town"], view.Editor.Areas.Select(a => a.Name));
        Assert.Equal(-1, view.SelectedIndex);
    }

    [Fact]
    public async Task WorldAreaMapView_EditsDuringSave_AreRefusedAndSavedListUnchanged()
    {
        var plugin = new MemoryPlugin { BlockWrite = true };
        using var view = NewView(plugin);
        view.Select(0);

        var save = view.Editor.SaveAsync();
        Assert.True(plugin.InWrite.Wait(5000));
        Assert.True(view.Editor.IsSaving);
        Assert.Throws<InvalidOperationException>(() => view.RenameSelected("X"));
        Assert.Throws<InvalidOperationException>(() => view.DeleteSelected());
        Assert.Throws<InvalidOperationException>(() => view.Editor.Create("TownRegion", "N", new WorldAreaRect(0, 0, 1, 1, 0, 0)));
        Assert.Throws<InvalidOperationException>(() => view.Editor.SetRect(0, 0, new WorldAreaRect(0, 0, 1, 1, 0, 0)));

        plugin.ReleaseWrite.Set();
        await save;

        Assert.False(view.Editor.IsSaving);
        Assert.Equal(["Town", "Keep"], plugin.Saved.Select(a => a.Name));
        view.RenameSelected("Y"); // allowed again
    }

    [Fact]
    public void WorldAreaMapView_ZoomAt_KeepsPointUnderPointerFixed()
    {
        using var view = NewView(new MemoryPlugin());
        view.SetView(50, 70, 2);
        var before = view.ToWorld(120, 80);

        view.ZoomAt(120, 80, 1.25);
        view.ZoomAt(120, 80, 0.8);
        view.ZoomAt(120, 80, 3);

        var after = view.ToWorld(120, 80);
        Assert.Equal(before.X, after.X, 6);
        Assert.Equal(before.Y, after.Y, 6);
        Assert.NotEqual(2, view.Zoom);
    }

    [Fact]
    public void WorldAreaMapView_LargeRange_UsesStrideToBoundReads()
    {
        var reader = new CountingReader();
        using var view = new WorldAreaMapView(WorldAreaEditor.Open(new MemoryPlugin(), "w")!, reader, _ => default, loadLimit: 16);
        view.SetView(0, 0, 1);

        view.LoadVisible(512, 512);

        Assert.True(view.Stride(512, 512) > 1);
        Assert.True(reader.Reads.Count <= 256);
    }

    [Fact]
    public async Task WorldAreaMapView_SaveReloadWhileIterating_SnapshotStaysConsistent()
    {
        var plugin = new MemoryPlugin();
        using var view = NewView(plugin);
        var snapshot = view.Editor.Areas;
        var stop = false;
        Exception? failure = null;
        var reader = Task.Run(() =>
        {
            try
            {
                while (!Volatile.Read(ref stop))
                {
                    var areas = view.Editor.Areas; // what a render does: one reference, then index
                    for (var i = 0; i < areas.Count; i++)
                    {
                        _ = areas[i].Rects.Count;
                    }
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        for (var n = 0; n < 200; n++)
        {
            await view.Editor.SaveAsync();
        }

        Volatile.Write(ref stop, true);
        await reader;

        Assert.Null(failure);
        Assert.Equal(2, snapshot.Count); // the list taken before the saves was never cleared
        Assert.NotSame(snapshot, view.Editor.Areas);
    }
}
