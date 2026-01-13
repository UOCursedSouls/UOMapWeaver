using System.Buffers.Binary;

namespace UOMapWeaver.Core.Bmp;

public static class BmpCodec
{
    public static bool TryReadInfo(string path, out int width, out int height, out short bitsPerPixel)
    {
        width = 0;
        height = 0;
        bitsPerPixel = 0;

        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> header = stackalloc byte[54];
            if (stream.Read(header) < header.Length)
            {
                return false;
            }

            if (header[0] != (byte)'B' || header[1] != (byte)'M')
            {
                return false;
            }

            width = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(18, 4));
            height = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(22, 4));
            bitsPerPixel = BinaryPrimitives.ReadInt16LittleEndian(header.Slice(28, 2));

            if (height < 0)
            {
                height = Math.Abs(height);
            }

            return width > 0 && height > 0;
        }
        catch
        {
            return false;
        }
    }
}
