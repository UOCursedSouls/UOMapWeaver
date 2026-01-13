using System.Buffers.Binary;

namespace UOMapWeaver.Core.Bmp;

public sealed class Bmp24StreamWriter : IDisposable
{
    private const int FileHeaderSize = 14;
    private const int DibHeaderSize = 40;

    private readonly FileStream _stream;
    private readonly int _rowSize;
    private readonly int _height;
    private readonly int _width;
    private int _rowsWritten;
    private readonly byte[] _rowBuffer;
    private bool _disposed;

    public Bmp24StreamWriter(string path, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "BMP dimensions must be positive.");
        }

        _height = height;
        _width = width;
        _rowSize = GetRowSize(width);
        _rowBuffer = new byte[_rowSize];

        var pixelDataSize = _rowSize * height;
        var pixelOffset = FileHeaderSize + DibHeaderSize;
        var fileSize = pixelOffset + pixelDataSize;

        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        Span<byte> header = stackalloc byte[FileHeaderSize + DibHeaderSize];
        header.Clear();

        header[0] = (byte)'B';
        header[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(2, 4), fileSize);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(10, 4), pixelOffset);

        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(14, 4), DibHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(18, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(22, 4), height);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(26, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(28, 2), 24);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(30, 4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(34, 4), pixelDataSize);

        _stream.Write(header);
    }

    public void WriteRow(ReadOnlySpan<byte> row)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(Bmp24StreamWriter));
        }

        if (_rowsWritten >= _height)
        {
            throw new InvalidOperationException("All rows already written.");
        }

        var expectedLength = _width * 3;
        if (row.Length < expectedLength)
        {
            throw new ArgumentException("Row length is smaller than expected.", nameof(row));
        }

        Array.Clear(_rowBuffer, 0, _rowBuffer.Length);
        row[..expectedLength].CopyTo(_rowBuffer);
        _stream.Write(_rowBuffer, 0, _rowBuffer.Length);
        _rowsWritten++;
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
