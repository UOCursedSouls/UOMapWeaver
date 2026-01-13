using System.Buffers.Binary;

namespace UOMapWeaver.Core.Bmp;

public sealed class Bmp24StreamReader : IDisposable
{
    private const int FileHeaderSize = 14;
    private const int DibHeaderSize = 40;

    private readonly FileStream _stream;
    private readonly int _rowSize;
    private readonly int _height;
    private readonly int _width;
    private readonly int _pixelOffset;
    private readonly bool _isTopDown;
    private readonly byte[] _rowBuffer;
    private bool _disposed;

    public Bmp24StreamReader(string path)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[FileHeaderSize + DibHeaderSize];
        if (_stream.Read(header) != header.Length)
        {
            throw new InvalidDataException("Invalid BMP header.");
        }

        if (header[0] != (byte)'B' || header[1] != (byte)'M')
        {
            throw new InvalidDataException("Not a BMP file.");
        }

        _pixelOffset = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(10, 4));
        var dibSize = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(14, 4));
        if (dibSize < DibHeaderSize)
        {
            throw new InvalidDataException("Unsupported BMP header size.");
        }

        _width = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(18, 4));
        var heightValue = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(22, 4));
        var planes = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(26, 2));
        var bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(28, 2));
        var compression = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(30, 4));

        if (planes != 1 || bitsPerPixel != 24 || compression != 0)
        {
            throw new InvalidDataException("Only 24-bit uncompressed BMP files are supported.");
        }

        _isTopDown = heightValue < 0;
        _height = Math.Abs(heightValue);
        _rowSize = GetRowSize(_width);
        _rowBuffer = new byte[_rowSize];
    }

    public int Width => _width;

    public int Height => _height;

    public bool IsTopDown => _isTopDown;

    public void ReadRow(int y, Span<byte> destination)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(Bmp24StreamReader));
        }

        if (y < 0 || y >= _height)
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }

        var expectedLength = _width * 3;
        if (destination.Length < expectedLength)
        {
            throw new ArgumentException("Destination buffer is too small.", nameof(destination));
        }

        var fileRow = _isTopDown ? y : _height - 1 - y;
        var offset = _pixelOffset + (long)fileRow * _rowSize;
        _stream.Seek(offset, SeekOrigin.Begin);
        _stream.ReadExactly(_rowBuffer, 0, _rowBuffer.Length);
        _rowBuffer.AsSpan(0, expectedLength).CopyTo(destination);
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

    private static int GetRowSize(int width)
    {
        var rowSize = width * 3;
        return (rowSize + 3) & ~3;
    }
}
