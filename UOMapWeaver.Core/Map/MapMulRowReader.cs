using System.Buffers.Binary;

namespace UOMapWeaver.Core.Map;

public sealed class MapMulRowReader : IDisposable
{
    private readonly FileStream _stream;
    private readonly int _width;
    private readonly int _height;
    private readonly int _blockWidth;
    private readonly int _blockHeight;
    private readonly byte[] _buffer;
    private bool _disposed;

    public MapMulRowReader(string mapMulPath, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Map size must be positive.");
        }

        _width = width;
        _height = height;
        _blockWidth = width / MapMul.BlockSize;
        _blockHeight = height / MapMul.BlockSize;
        _buffer = new byte[MapMul.LandBlockBytes];
        _stream = new FileStream(mapMulPath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public void ReadRow(int y, Span<LandTile> row)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(MapMulRowReader));
        }

        if (y < 0 || y >= _height)
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }

        if (row.Length < _width)
        {
            throw new ArgumentException("Destination row buffer too small.", nameof(row));
        }

        var blockY = y / MapMul.BlockSize;
        var localY = y % MapMul.BlockSize;

        for (var bx = 0; bx < _blockWidth; bx++)
        {
            var offset = (long)(bx * _blockHeight + blockY) * MapMul.LandBlockBytes;
            _stream.Seek(offset, SeekOrigin.Begin);
            _stream.ReadExactly(_buffer, 0, _buffer.Length);

            var span = _buffer.AsSpan(MapMul.LandHeaderBytes);
            for (var localX = 0; localX < MapMul.BlockSize; localX++)
            {
                var i = localY * MapMul.BlockSize + localX;
                var tileId = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(i * MapMul.LandTileBytes, 2));
                var z = unchecked((sbyte)span[i * MapMul.LandTileBytes + 2]);
                row[bx * MapMul.BlockSize + localX] = new LandTile(tileId, z);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
    }
}
