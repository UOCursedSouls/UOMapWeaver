using System.IO.Compression;
using System.Security.Cryptography;

namespace UOMapWeaver.Core.ResourcePack;

/// <summary>
/// Minimal PNG writer using only built-in .NET types (no SkiaSharp/ImageSharp needed).
/// Writes 24-bit RGB PNGs with zlib compression.
/// </summary>
public static class PngWriter
{
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Write an RGBA pixel buffer as a 24-bit PNG file (alpha is discarded).
    /// </summary>
    public static void WriteFromRgba(string path, int width, int height, byte[] rgba)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);

        // PNG signature
        stream.Write(PngHeader);

        // IHDR chunk
        var ihdr = new byte[13];
        WriteInt32BE(ihdr, 0, width);
        WriteInt32BE(ihdr, 4, height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // color type: RGBA (with alpha transparency)
        ihdr[10] = 0; // compression
        ihdr[11] = 0; // filter
        ihdr[12] = 0; // interlace
        WriteChunk(stream, "IHDR", ihdr);

        // IDAT chunk(s) — compress scanlines with zlib
        var rawData = new byte[height * (1 + width * 4)]; // filter byte + RGBA per row
        var pos = 0;
        for (int y = 0; y < height; y++)
        {
            rawData[pos++] = 0; // filter: None
            for (int x = 0; x < width; x++)
            {
                var srcIdx = (y * width + x) * 4;
                rawData[pos++] = rgba[srcIdx];     // R
                rawData[pos++] = rgba[srcIdx + 1]; // G
                rawData[pos++] = rgba[srcIdx + 2]; // B
                rawData[pos++] = rgba[srcIdx + 3]; // A
            }
        }

        // Compress with zlib (deflate + zlib header)
        using var compressedStream = new MemoryStream();
        // Zlib header: CMF=0x78 (deflate, window=32K), FLG=0x01 (no dict, check bits)
        compressedStream.WriteByte(0x78);
        compressedStream.WriteByte(0x01);
        using (var deflate = new DeflateStream(compressedStream, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(rawData, 0, rawData.Length);
        }

        // Adler-32 checksum
        var adler = Adler32(rawData);
        compressedStream.WriteByte((byte)(adler >> 24));
        compressedStream.WriteByte((byte)(adler >> 16));
        compressedStream.WriteByte((byte)(adler >> 8));
        compressedStream.WriteByte((byte)adler);

        WriteChunk(stream, "IDAT", compressedStream.ToArray());

        // IEND chunk
        WriteChunk(stream, "IEND", []);
    }

    /// <summary>
    /// Write a BGR pixel buffer (from BMP data) as a 24-bit PNG file.
    /// </summary>
    public static void WriteFromBgr(string path, int width, int height, byte[] bgr, int rowStride)
    {
        // Convert to RGBA first
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var srcIdx = y * rowStride + x * 3;
                var dstIdx = (y * width + x) * 4;
                rgba[dstIdx] = bgr[srcIdx + 2];     // R
                rgba[dstIdx + 1] = bgr[srcIdx + 1]; // G
                rgba[dstIdx + 2] = bgr[srcIdx];     // B
                rgba[dstIdx + 3] = 255;
            }
        }

        WriteFromRgba(path, width, height, rgba);
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var lengthBytes = new byte[4];
        WriteInt32BE(lengthBytes, 0, data.Length);
        stream.Write(lengthBytes);

        var typeBytes = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            typeBytes[i] = (byte)type[i];
        }

        stream.Write(typeBytes);
        stream.Write(data);

        // CRC32 over type + data
        var crcData = new byte[4 + data.Length];
        Array.Copy(typeBytes, crcData, 4);
        Array.Copy(data, 0, crcData, 4, data.Length);
        var crc = Crc32(crcData);
        var crcBytes = new byte[4];
        WriteInt32BE(crcBytes, 0, (int)crc);
        stream.Write(crcBytes);
    }

    private static void WriteInt32BE(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        for (int i = 0; i < data.Length; i++)
        {
            a = (a + data[i]) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }

    // CRC32 lookup table (PNG uses CRC-32/ISO-HDLC)
    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (int j = 0; j < 8; j++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFF;
    }
}
