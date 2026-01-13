using System.Buffers.Binary;

namespace UOMapWeaver.Core.Map;

public static class MapMulCodec
{
    public static LandTile[] ReadLandTiles(string mapMulPath, int width, int height)
    {
        var blockWidth = width / MapMul.BlockSize;
        var blockHeight = height / MapMul.BlockSize;
        var tiles = new LandTile[width * height];

        using var stream = new FileStream(mapMulPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[MapMul.LandBlockBytes];

        for (var bx = 0; bx < blockWidth; bx++)
        {
            for (var by = 0; by < blockHeight; by++)
            {
                var offset = (long)(bx * blockHeight + by) * MapMul.LandBlockBytes;
                stream.Seek(offset, SeekOrigin.Begin);
                stream.ReadExactly(buffer, 0, buffer.Length);

                var span = buffer.AsSpan(MapMul.LandHeaderBytes);
                for (var i = 0; i < MapMul.LandTilesPerBlock; i++)
                {
                    var tileId = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(i * MapMul.LandTileBytes, 2));
                    var z = unchecked((sbyte)span[i * MapMul.LandTileBytes + 2]);

                    var localX = i & 0x7;
                    var localY = i >> 3;
                    var x = bx * MapMul.BlockSize + localX;
                    var y = by * MapMul.BlockSize + localY;

                    tiles[y * width + x] = new LandTile(tileId, z);
                }
            }
        }

        return tiles;
    }

    public static LandTile[] ReadLandTilesRegion(
        string mapMulPath,
        int width,
        int height,
        int startX,
        int startY,
        int regionWidth,
        int regionHeight)
    {
        if (startX < 0 || startY < 0 ||
            regionWidth <= 0 || regionHeight <= 0 ||
            startX + regionWidth > width ||
            startY + regionHeight > height)
        {
            throw new ArgumentOutOfRangeException(nameof(startX), "Region is outside map bounds.");
        }

        var blockWidth = width / MapMul.BlockSize;
        var blockHeight = height / MapMul.BlockSize;
        var tiles = new LandTile[regionWidth * regionHeight];

        var startBlockX = startX / MapMul.BlockSize;
        var startBlockY = startY / MapMul.BlockSize;
        var endBlockX = (startX + regionWidth - 1) / MapMul.BlockSize;
        var endBlockY = (startY + regionHeight - 1) / MapMul.BlockSize;

        using var stream = new FileStream(mapMulPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[MapMul.LandBlockBytes];

        for (var bx = startBlockX; bx <= endBlockX; bx++)
        {
            for (var by = startBlockY; by <= endBlockY; by++)
            {
                var offset = (long)(bx * blockHeight + by) * MapMul.LandBlockBytes;
                stream.Seek(offset, SeekOrigin.Begin);
                stream.ReadExactly(buffer, 0, buffer.Length);

                var span = buffer.AsSpan(MapMul.LandHeaderBytes);
                for (var i = 0; i < MapMul.LandTilesPerBlock; i++)
                {
                    var localX = i & 0x7;
                    var localY = i >> 3;
                    var x = bx * MapMul.BlockSize + localX;
                    var y = by * MapMul.BlockSize + localY;

                    if (x < startX || y < startY || x >= startX + regionWidth || y >= startY + regionHeight)
                    {
                        continue;
                    }

                    var tileId = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(i * MapMul.LandTileBytes, 2));
                    var z = unchecked((sbyte)span[i * MapMul.LandTileBytes + 2]);

                    var destX = x - startX;
                    var destY = y - startY;
                    tiles[destY * regionWidth + destX] = new LandTile(tileId, z);
                }
            }
        }

        return tiles;
    }

    public static void WriteLandTiles(string mapMulPath, int width, int height, LandTile[] tiles)
    {
        var blockWidth = width / MapMul.BlockSize;
        var blockHeight = height / MapMul.BlockSize;

        using var stream = new FileStream(mapMulPath, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[MapMul.LandBlockBytes];

        for (var bx = 0; bx < blockWidth; bx++)
        {
            for (var by = 0; by < blockHeight; by++)
            {
                Array.Clear(buffer, 0, buffer.Length);
                var span = buffer.AsSpan(MapMul.LandHeaderBytes);

                for (var i = 0; i < MapMul.LandTilesPerBlock; i++)
                {
                    var localX = i & 0x7;
                    var localY = i >> 3;
                    var x = bx * MapMul.BlockSize + localX;
                    var y = by * MapMul.BlockSize + localY;
                    var tile = tiles[y * width + x];

                    BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(i * MapMul.LandTileBytes, 2), tile.TileId);
                    span[i * MapMul.LandTileBytes + 2] = unchecked((byte)tile.Z);
                }

                stream.Write(buffer, 0, buffer.Length);
            }
        }
    }

    public static void WriteLandTilesFromRows(
        string mapMulPath,
        int width,
        int height,
        Action<int, Span<LandTile>> rowProvider)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Map size must be positive.");
        }

        var blockWidth = width / MapMul.BlockSize;
        var blockHeight = height / MapMul.BlockSize;

        using var stream = new FileStream(mapMulPath, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.SetLength((long)blockWidth * blockHeight * MapMul.LandBlockBytes);

        var rowBuffer = new LandTile[width * MapMul.BlockSize];
        var blockBuffer = new byte[MapMul.LandBlockBytes];

        for (var by = 0; by < blockHeight; by++)
        {
            for (var localY = 0; localY < MapMul.BlockSize; localY++)
            {
                var y = by * MapMul.BlockSize + localY;
                var rowSpan = rowBuffer.AsSpan(localY * width, width);
                rowProvider(y, rowSpan);
            }

            for (var bx = 0; bx < blockWidth; bx++)
            {
                Array.Clear(blockBuffer, 0, blockBuffer.Length);
                var span = blockBuffer.AsSpan(MapMul.LandHeaderBytes);

                for (var localY = 0; localY < MapMul.BlockSize; localY++)
                {
                    var rowBase = localY * width + bx * MapMul.BlockSize;
                    for (var localX = 0; localX < MapMul.BlockSize; localX++)
                    {
                        var tile = rowBuffer[rowBase + localX];
                        var i = localY * MapMul.BlockSize + localX;
                        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(i * MapMul.LandTileBytes, 2), tile.TileId);
                        span[i * MapMul.LandTileBytes + 2] = unchecked((byte)tile.Z);
                    }
                }

                var offset = (long)(bx * blockHeight + by) * MapMul.LandBlockBytes;
                stream.Seek(offset, SeekOrigin.Begin);
                stream.Write(blockBuffer, 0, blockBuffer.Length);
            }
        }
    }
}

