using System.Buffers.Binary;
using System.Collections.Generic;

namespace UOMapWeaver.Core.Map;

public sealed class VerdataMul
{
    private readonly string _path;
    private readonly Dictionary<int, LandTile[]> _mapBlocks;
    private readonly Dictionary<int, List<StaticMulEntry>> _staticsBlocks;

    private VerdataMul(
        string path,
        int? mapFileId,
        int? staticsFileId,
        Dictionary<int, LandTile[]> mapBlocks,
        Dictionary<int, List<StaticMulEntry>> staticsBlocks)
    {
        _path = path;
        MapFileId = mapFileId;
        StaticsFileId = staticsFileId;
        _mapBlocks = mapBlocks;
        _staticsBlocks = staticsBlocks;
    }

    public int? MapFileId { get; }

    public int? StaticsFileId { get; }

    public int MapPatchCount => _mapBlocks.Count;

    public int StaticsPatchCount => _staticsBlocks.Count;

    public static VerdataMul Load(string path, int blockCount, int? mapFileIdOverride = null, int? staticsFileIdOverride = null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);

        var entryCount = reader.ReadInt32();
        var entries = new List<VerdataEntry>(Math.Max(0, entryCount));
        var mapCandidates = new Dictionary<int, int>();
        var staticsCandidates = new Dictionary<int, int>();

        for (var i = 0; i < entryCount; i++)
        {
            var fileId = reader.ReadInt32();
            var blockId = reader.ReadInt32();
            var offset = reader.ReadInt32();
            var length = reader.ReadInt32();
            var extra = reader.ReadInt32();

            if (blockId < 0 || length <= 0)
            {
                continue;
            }

            var entry = new VerdataEntry(fileId, blockId, offset, length, extra);
            entries.Add(entry);

            if (blockId >= blockCount)
            {
                continue;
            }

            if (length == MapMul.LandBlockBytes)
            {
                mapCandidates[fileId] = mapCandidates.TryGetValue(fileId, out var count) ? count + 1 : 1;
            }

            if (length % MapMul.StaticTileBytes == 0)
            {
                staticsCandidates[fileId] = staticsCandidates.TryGetValue(fileId, out var count) ? count + 1 : 1;
            }
        }

        var mapFileId = mapFileIdOverride ?? PickBestCandidate(mapCandidates);
        var staticsFileId = staticsFileIdOverride ?? PickBestCandidate(staticsCandidates);

        var mapBlocks = new Dictionary<int, LandTile[]>();
        var staticsBlocks = new Dictionary<int, List<StaticMulEntry>>();

        if (entries.Count > 0)
        {
            stream.Seek(0, SeekOrigin.Begin);
            reader.ReadInt32();

            foreach (var entry in entries)
            {
                if (entry.BlockId < 0 || entry.BlockId >= blockCount)
                {
                    continue;
                }

                if (mapFileId.HasValue && entry.FileId == mapFileId.Value &&
                    entry.Length >= MapMul.LandBlockBytes)
                {
                    var buffer = new byte[entry.Length];
                    stream.Seek(entry.Offset, SeekOrigin.Begin);
                    stream.ReadExactly(buffer, 0, entry.Length);
                    var block = ParseMapBlock(buffer);
                    mapBlocks[entry.BlockId] = block;
                    continue;
                }

                if (staticsFileId.HasValue && entry.FileId == staticsFileId.Value &&
                    entry.Length % MapMul.StaticTileBytes == 0)
                {
                    var buffer = new byte[entry.Length];
                    stream.Seek(entry.Offset, SeekOrigin.Begin);
                    stream.ReadExactly(buffer, 0, entry.Length);
                    var list = ParseStaticsBlock(buffer);
                    staticsBlocks[entry.BlockId] = list;
                }
            }
        }

        return new VerdataMul(path, mapFileId, staticsFileId, mapBlocks, staticsBlocks);
    }

    public bool TryGetMapBlock(int blockId, out LandTile[] block)
        => _mapBlocks.TryGetValue(blockId, out block!);

    public void ApplyToLandTiles(LandTile[] tiles, int width, int height)
    {
        if (_mapBlocks.Count == 0)
        {
            return;
        }

        var blockWidth = width / MapMul.BlockSize;
        var blockHeight = height / MapMul.BlockSize;
        var maxBlocks = blockWidth * blockHeight;

        foreach (var (blockId, blockTiles) in _mapBlocks)
        {
            if (blockId < 0 || blockId >= maxBlocks)
            {
                continue;
            }

            var bx = blockId / blockHeight;
            var by = blockId % blockHeight;
            for (var i = 0; i < MapMul.LandTilesPerBlock; i++)
            {
                var localX = i & 0x7;
                var localY = i >> 3;
                var x = bx * MapMul.BlockSize + localX;
                var y = by * MapMul.BlockSize + localY;
                tiles[y * width + x] = blockTiles[i];
            }
        }
    }

    public void ApplyToStatics(List<StaticMulEntry>[] blocks)
    {
        if (_staticsBlocks.Count == 0)
        {
            return;
        }

        foreach (var (blockId, list) in _staticsBlocks)
        {
            if (blockId < 0 || blockId >= blocks.Length)
            {
                continue;
            }

            blocks[blockId] = new List<StaticMulEntry>(list);
        }
    }

    private static int? PickBestCandidate(Dictionary<int, int> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var best = candidates.OrderByDescending(pair => pair.Value).First();
        return best.Key;
    }

    private static LandTile[] ParseMapBlock(byte[] buffer)
    {
        if (buffer.Length < MapMul.LandBlockBytes)
        {
            return new LandTile[MapMul.LandTilesPerBlock];
        }

        var block = new LandTile[MapMul.LandTilesPerBlock];
        var span = buffer.AsSpan(MapMul.LandHeaderBytes, MapMul.LandBlockBytes - MapMul.LandHeaderBytes);
        for (var i = 0; i < MapMul.LandTilesPerBlock; i++)
        {
            var tileId = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(i * MapMul.LandTileBytes, 2));
            var z = unchecked((sbyte)span[i * MapMul.LandTileBytes + 2]);
            block[i] = new LandTile(tileId, z);
        }

        return block;
    }

    private static List<StaticMulEntry> ParseStaticsBlock(byte[] buffer)
    {
        var count = buffer.Length / MapMul.StaticTileBytes;
        var list = new List<StaticMulEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var offset = i * MapMul.StaticTileBytes;
            var tileId = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset, 2));
            var x = buffer[offset + 2];
            var y = buffer[offset + 3];
            var z = unchecked((sbyte)buffer[offset + 4]);
            var hue = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 5, 2));
            list.Add(new StaticMulEntry(tileId, x, y, z, hue));
        }

        return list;
    }

    private readonly record struct VerdataEntry(int FileId, int BlockId, int Offset, int Length, int Extra);
}
