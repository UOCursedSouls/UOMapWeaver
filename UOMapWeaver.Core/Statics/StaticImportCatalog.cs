using UOMapWeaver.Core.Map;

namespace UOMapWeaver.Core.Statics;

public static class StaticImportCatalog
{
    public static List<StaticImportEntry> LoadStaticTiles(IEnumerable<string> roots, out StaticImportSourceInfo info)
    {
        var entries = new List<StaticImportEntry>();
        var fileCount = 0;
        var tileCount = 0;

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*.xml", SearchOption.AllDirectories))
            {
                var tiles = StaticImportXmlImporter.LoadStaticTilesFromXml(file);
                if (tiles.Count == 0)
                {
                    continue;
                }

                fileCount++;
                tileCount += tiles.Count;
                entries.AddRange(tiles);
            }
        }

        info = new StaticImportSourceInfo(fileCount, tileCount);
        return entries;
    }

    public static void AddImportedStatics(
        List<StaticMulEntry>[] blocks,
        IReadOnlyList<StaticImportEntry> entries,
        int width,
        int height,
        StaticsLayout layout,
        out int added,
        out int skippedOutOfBounds)
    {
        added = 0;
        skippedOutOfBounds = 0;

        if (entries.Count == 0)
        {
            return;
        }

        var blockWidth = width / MapMul.BlockSize;
        var blockHeight = height / MapMul.BlockSize;
        var blockCount = blockWidth * blockHeight;
        if (blocks.Length < blockCount)
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (entry.X < 0 || entry.Y < 0 || entry.X >= width || entry.Y >= height)
            {
                skippedOutOfBounds++;
                continue;
            }

            var blockX = entry.X / MapMul.BlockSize;
            var blockY = entry.Y / MapMul.BlockSize;
            var blockIndex = StaticsLayoutHelper.GetBlockIndex(blockX, blockY, blockWidth, blockHeight, layout);
            if (blockIndex < 0 || blockIndex >= blockCount)
            {
                skippedOutOfBounds++;
                continue;
            }

            var list = blocks[blockIndex] ??= new List<StaticMulEntry>();
            var localX = (byte)(entry.X % MapMul.BlockSize);
            var localY = (byte)(entry.Y % MapMul.BlockSize);
            list.Add(new StaticMulEntry(entry.TileId, localX, localY, entry.Z, entry.Hue));
            added++;
        }
    }
}

public readonly record struct StaticImportSourceInfo(int FileCount, int TileCount);
