using System.Buffers.Binary;

namespace UOMapWeaver.Core.Map;

public static class StaticMulCodec
{
    public static List<StaticMulEntry>[] ReadStatics(string staIdxPath, string staticsPath, int width, int height)
    {
        var blockWidth = width / MapMul.BlockSize;
        var blockHeight = height / MapMul.BlockSize;
        var blockCount = blockWidth * blockHeight;
        var results = new List<StaticMulEntry>[blockCount];

        using var idxStream = new FileStream(staIdxPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var staticsStream = new FileStream(staticsPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        Span<byte> record = stackalloc byte[MapMul.StaticIndexRecordBytes];
        for (var i = 0; i < blockCount; i++)
        {
            if (idxStream.Read(record) != MapMul.StaticIndexRecordBytes)
            {
                break;
            }

            var lookup = BinaryPrimitives.ReadInt32LittleEndian(record.Slice(0, 4));
            var length = BinaryPrimitives.ReadInt32LittleEndian(record.Slice(4, 4));

            if (lookup < 0 || length <= 0)
            {
                continue;
            }

            var count = length / MapMul.StaticTileBytes;
            if (count <= 0)
            {
                continue;
            }

            var list = new List<StaticMulEntry>(count);
            staticsStream.Seek(lookup, SeekOrigin.Begin);
            var buffer = new byte[length];
            var read = staticsStream.Read(buffer, 0, length);
            if (read < length)
            {
                continue;
            }

            for (var j = 0; j < count; j++)
            {
                var offset = j * MapMul.StaticTileBytes;
                var tileId = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset, 2));
                var x = buffer[offset + 2];
                var y = buffer[offset + 3];
                var z = unchecked((sbyte)buffer[offset + 4]);
                var hue = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 5, 2));
                list.Add(new StaticMulEntry(tileId, x, y, z, hue));
            }

            results[i] = list;
        }

        return results;
    }

    public static void WriteEmptyStatics(string staIdxPath, string staticsPath, int width, int height)
    {
        var blockWidth = width / MapMul.BlockSize;
        var blockHeight = height / MapMul.BlockSize;
        var blockCount = blockWidth * blockHeight;

        using var idxStream = new FileStream(staIdxPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var staticsStream = new FileStream(staticsPath, FileMode.Create, FileAccess.Write, FileShare.None);

        Span<byte> record = stackalloc byte[MapMul.StaticIndexRecordBytes];
        BinaryPrimitives.WriteInt32LittleEndian(record.Slice(0, 4), -1);
        BinaryPrimitives.WriteInt32LittleEndian(record.Slice(4, 4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(record.Slice(8, 4), 0);

        for (var i = 0; i < blockCount; i++)
        {
            idxStream.Write(record);
        }
    }

    public static void WriteStatics(
        string staIdxPath,
        string staticsPath,
        int width,
        int height,
        IReadOnlyList<List<StaticMulEntry>> blocks)
    {
        var blockWidth = width / MapMul.BlockSize;
        var blockHeight = height / MapMul.BlockSize;
        var blockCount = blockWidth * blockHeight;

        using var idxStream = new FileStream(staIdxPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var staticsStream = new FileStream(staticsPath, FileMode.Create, FileAccess.Write, FileShare.None);

        Span<byte> record = stackalloc byte[MapMul.StaticIndexRecordBytes];
        var offset = 0;

        for (var i = 0; i < blockCount; i++)
        {
            var list = i < blocks.Count ? blocks[i] : null;
            if (list is null || list.Count == 0)
            {
                BinaryPrimitives.WriteInt32LittleEndian(record.Slice(0, 4), -1);
                BinaryPrimitives.WriteInt32LittleEndian(record.Slice(4, 4), 0);
                BinaryPrimitives.WriteInt32LittleEndian(record.Slice(8, 4), 0);
                idxStream.Write(record);
                continue;
            }

            var length = list.Count * MapMul.StaticTileBytes;
            BinaryPrimitives.WriteInt32LittleEndian(record.Slice(0, 4), offset);
            BinaryPrimitives.WriteInt32LittleEndian(record.Slice(4, 4), length);
            BinaryPrimitives.WriteInt32LittleEndian(record.Slice(8, 4), 0);
            idxStream.Write(record);

            var tile = new byte[MapMul.StaticTileBytes];
            foreach (var entry in list)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(tile.AsSpan(0, 2), entry.TileId);
                tile[2] = entry.X;
                tile[3] = entry.Y;
                tile[4] = unchecked((byte)entry.Z);
                BinaryPrimitives.WriteUInt16LittleEndian(tile.AsSpan(5, 2), entry.Hue);
                staticsStream.Write(tile);
            }

            offset += length;
        }
    }
}

public readonly record struct StaticMulEntry(ushort TileId, byte X, byte Y, sbyte Z, ushort Hue);

