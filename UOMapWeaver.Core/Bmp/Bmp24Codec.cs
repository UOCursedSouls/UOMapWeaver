using System.Buffers.Binary;

namespace UOMapWeaver.Core.Bmp;

public static class Bmp24Codec
{
    private const int FileHeaderSize = 14;
    private const int DibHeaderSize = 40;

    public static Bmp24Image Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);

        if (reader.ReadByte() != (byte)'B' || reader.ReadByte() != (byte)'M')
        {
            throw new InvalidDataException("Not a BMP file.");
        }

        _ = reader.ReadInt32();
        _ = reader.ReadInt16();
        _ = reader.ReadInt16();
        var pixelOffset = reader.ReadInt32();

        var dibSize = reader.ReadInt32();
        if (dibSize < DibHeaderSize)
        {
            throw new InvalidDataException("Unsupported BMP DIB header.");
        }

        var width = reader.ReadInt32();
        var height = reader.ReadInt32();
        var planes = reader.ReadInt16();
        var bitsPerPixel = reader.ReadInt16();
        var compression = reader.ReadInt32();

        if (planes != 1 || bitsPerPixel != 24 || compression != 0)
        {
            throw new InvalidDataException("Unsupported BMP format (expected 24-bit, uncompressed).");
        }

        var topDown = false;
        if (height < 0)
        {
            height = Math.Abs(height);
            topDown = true;
        }

        stream.Seek(pixelOffset, SeekOrigin.Begin);

        var rowStride = GetRowStride(width);
        var pixels = new byte[width * height * 3];
        var rowBuffer = new byte[rowStride];

        for (var row = 0; row < height; row++)
        {
            stream.ReadExactly(rowBuffer, 0, rowStride);
            var targetRow = topDown ? row : (height - 1 - row);
            var targetOffset = targetRow * width * 3;

            for (var x = 0; x < width; x++)
            {
                var src = x * 3;
                var dst = targetOffset + x * 3;
                pixels[dst] = rowBuffer[src + 2];
                pixels[dst + 1] = rowBuffer[src + 1];
                pixels[dst + 2] = rowBuffer[src];
            }
        }

        return new Bmp24Image(width, height, pixels);
    }

    public static void Write(string path, Bmp24Image image)
    {
        var rowStride = GetRowStride(image.Width);
        var dataSize = rowStride * image.Height;
        var fileSize = FileHeaderSize + DibHeaderSize + dataSize;

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);

        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(fileSize);
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Write(FileHeaderSize + DibHeaderSize);

        writer.Write(DibHeaderSize);
        writer.Write(image.Width);
        writer.Write(image.Height);
        writer.Write((short)1);
        writer.Write((short)24);
        writer.Write(0);
        writer.Write(dataSize);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);

        var padding = new byte[rowStride - image.Width * 3];
        for (var row = image.Height - 1; row >= 0; row--)
        {
            var offset = row * image.Width * 3;
            for (var x = 0; x < image.Width; x++)
            {
                var src = offset + x * 3;
                writer.Write(image.Pixels[src + 2]);
                writer.Write(image.Pixels[src + 1]);
                writer.Write(image.Pixels[src]);
            }

            if (padding.Length > 0)
            {
                writer.Write(padding);
            }
        }
    }

    private static int GetRowStride(int width)
    {
        var raw = width * 3;
        return (raw + 3) & ~3;
    }
}
