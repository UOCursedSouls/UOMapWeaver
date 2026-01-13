using System.Buffers.Binary;

namespace UOMapWeaver.Core.Bmp;

public static class Bmp8Codec
{
    private const int FileHeaderSize = 14;
    private const int DibHeaderSize = 40;
    private const int PaletteEntries = 256;

    public static Bmp8Image Read(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < FileHeaderSize + DibHeaderSize)
        {
            throw new InvalidDataException("Invalid BMP file.");
        }

        if (data[0] != (byte)'B' || data[1] != (byte)'M')
        {
            throw new InvalidDataException("Not a BMP file.");
        }

        var pixelOffset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(10, 4));
        var dibSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(14, 4));
        if (dibSize < DibHeaderSize)
        {
            throw new InvalidDataException("Unsupported BMP header size.");
        }

        var width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(18, 4));
        var height = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(22, 4));
        var planes = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(26, 2));
        var bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(28, 2));
        var compression = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(30, 4));

        if (planes != 1 || bitsPerPixel != 8 || compression != 0)
        {
            throw new InvalidDataException("Only 8-bit uncompressed BMP files are supported.");
        }

        var absHeight = Math.Abs(height);
        var palette = ReadPalette(data);
        var pixels = new byte[width * absHeight];

        var rowSize = GetRowSize(width);
        var src = pixelOffset;

        if (height > 0)
        {
            for (var y = 0; y < absHeight; y++)
            {
                var row = data.AsSpan(src, rowSize);
                var dstY = absHeight - 1 - y;
                row[..width].CopyTo(pixels.AsSpan(dstY * width, width));
                src += rowSize;
            }
        }
        else
        {
            for (var y = 0; y < absHeight; y++)
            {
                var row = data.AsSpan(src, rowSize);
                row[..width].CopyTo(pixels.AsSpan(y * width, width));
                src += rowSize;
            }
        }

        return new Bmp8Image(width, absHeight, pixels, palette);
    }

    public static void Write(string path, Bmp8Image image)
    {
        if (image.Palette.Length != PaletteEntries)
        {
            throw new InvalidDataException("Palette must contain 256 entries.");
        }

        var rowSize = GetRowSize(image.Width);
        var pixelDataSize = rowSize * image.Height;
        var pixelOffset = FileHeaderSize + DibHeaderSize + PaletteEntries * 4;
        var fileSize = pixelOffset + pixelDataSize;

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        Span<byte> header = stackalloc byte[FileHeaderSize + DibHeaderSize];
        header.Clear();

        header[0] = (byte)'B';
        header[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(2, 4), fileSize);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(10, 4), pixelOffset);

        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(14, 4), DibHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(18, 4), image.Width);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(22, 4), image.Height);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(26, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(28, 2), 8);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(30, 4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(34, 4), pixelDataSize);

        stream.Write(header);
        WritePalette(stream, image.Palette);

        var rowBuffer = new byte[rowSize];
        for (var y = image.Height - 1; y >= 0; y--)
        {
            Array.Clear(rowBuffer, 0, rowBuffer.Length);
            Array.Copy(image.Pixels, y * image.Width, rowBuffer, 0, image.Width);
            stream.Write(rowBuffer, 0, rowBuffer.Length);
        }
    }

    private static BmpPaletteEntry[] ReadPalette(byte[] data)
    {
        var palette = new BmpPaletteEntry[PaletteEntries];
        var paletteStart = FileHeaderSize + DibHeaderSize;

        for (var i = 0; i < PaletteEntries; i++)
        {
            var offset = paletteStart + i * 4;
            var blue = data[offset];
            var green = data[offset + 1];
            var red = data[offset + 2];
            var alpha = data[offset + 3];
            palette[i] = new BmpPaletteEntry(blue, green, red, alpha);
        }

        return palette;
    }

    private static void WritePalette(Stream stream, BmpPaletteEntry[] palette)
    {
        var buffer = new byte[PaletteEntries * 4];
        for (var i = 0; i < PaletteEntries; i++)
        {
            var offset = i * 4;
            var entry = palette[i];
            buffer[offset] = entry.Blue;
            buffer[offset + 1] = entry.Green;
            buffer[offset + 2] = entry.Red;
            buffer[offset + 3] = entry.Alpha;
        }

        stream.Write(buffer, 0, buffer.Length);
    }

    private static int GetRowSize(int width)
    {
        var rowSize = width;
        return (rowSize + 3) & ~3;
    }

    public static BmpPaletteEntry[] CreateGrayscalePalette()
    {
        var palette = new BmpPaletteEntry[PaletteEntries];
        for (var i = 0; i < PaletteEntries; i++)
        {
            var value = (byte)i;
            palette[i] = new BmpPaletteEntry(value, value, value, 0);
        }
        return palette;
    }
}

