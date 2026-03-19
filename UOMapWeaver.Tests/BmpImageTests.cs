using System;
using System.IO;
using UOMapWeaver.Core.Bmp;

namespace UOMapWeaver.Tests;

public sealed class BmpImageTests : IDisposable
{
    private readonly string _tempDir;

    public BmpImageTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "UOMapWeaver.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    // --- Bmp8Image tests ---

    [Fact]
    public void Bmp8Image_ConstructorStoresProperties()
    {
        var palette = Bmp8Codec.CreateGrayscalePalette();
        var pixels = new byte[] { 0, 127, 255, 42 };
        var image = new Bmp8Image(2, 2, pixels, palette);

        Assert.Equal(2, image.Width);
        Assert.Equal(2, image.Height);
        Assert.Equal(4, image.Pixels.Length);
        Assert.Equal(256, image.Palette.Length);
    }

    [Fact]
    public void Bmp8Codec_SinglePixel_RoundTrip()
    {
        var palette = Bmp8Codec.CreateGrayscalePalette();
        var image = new Bmp8Image(1, 1, new byte[] { 42 }, palette);
        var path = Path.Combine(_tempDir, "single8.bmp");

        Bmp8Codec.Write(path, image);
        var loaded = Bmp8Codec.Read(path);

        Assert.Equal(1, loaded.Width);
        Assert.Equal(1, loaded.Height);
        Assert.Equal((byte)42, loaded.Pixels[0]);
    }

    [Fact]
    public void Bmp8Codec_NonSquare_RoundTrip()
    {
        var palette = Bmp8Codec.CreateGrayscalePalette();
        var width = 13;
        var height = 7;
        var pixels = new byte[width * height];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i % 256);
        }

        var image = new Bmp8Image(width, height, pixels, palette);
        var path = Path.Combine(_tempDir, "nonsquare8.bmp");

        Bmp8Codec.Write(path, image);
        var loaded = Bmp8Codec.Read(path);

        Assert.Equal(width, loaded.Width);
        Assert.Equal(height, loaded.Height);
        Assert.Equal(pixels, loaded.Pixels);
    }

    [Fact]
    public void Bmp8Codec_CustomPalette_PreservedOnRoundTrip()
    {
        var palette = new BmpPaletteEntry[256];
        for (var i = 0; i < 256; i++)
        {
            palette[i] = new BmpPaletteEntry((byte)(255 - i), (byte)(i / 2), (byte)i, 0);
        }

        var pixels = new byte[] { 0, 128, 255 };
        var image = new Bmp8Image(3, 1, pixels, palette);
        var path = Path.Combine(_tempDir, "custom_palette.bmp");

        Bmp8Codec.Write(path, image);
        var loaded = Bmp8Codec.Read(path);

        for (var i = 0; i < 256; i++)
        {
            Assert.Equal(palette[i].Red, loaded.Palette[i].Red);
            Assert.Equal(palette[i].Green, loaded.Palette[i].Green);
            Assert.Equal(palette[i].Blue, loaded.Palette[i].Blue);
        }
    }

    [Fact]
    public void Bmp8Image_ConstructorValidation()
    {
        var palette = Bmp8Codec.CreateGrayscalePalette();
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bmp8Image(0, 10, new byte[0], palette));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bmp8Image(10, 0, new byte[0], palette));
        Assert.Throws<ArgumentException>(() => new Bmp8Image(2, 2, new byte[3], palette));
    }

    // --- Bmp24Image tests ---

    [Fact]
    public void Bmp24Image_ConstructorValidation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bmp24Image(0, 10, new byte[0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bmp24Image(10, 0, new byte[0]));
        Assert.Throws<ArgumentException>(() => new Bmp24Image(2, 2, new byte[10]));
    }

    [Fact]
    public void Bmp24Image_StoresProperties()
    {
        var pixels = new byte[6]; // 2x1 * 3 bytes
        var image = new Bmp24Image(2, 1, pixels);

        Assert.Equal(2, image.Width);
        Assert.Equal(1, image.Height);
        Assert.Equal(6, image.Pixels.Length);
    }

    [Fact]
    public void Bmp24Codec_SinglePixel_RoundTrip()
    {
        var pixels = new byte[] { 200, 100, 50 }; // R=200, G=100, B=50
        var image = new Bmp24Image(1, 1, pixels);
        var path = Path.Combine(_tempDir, "single24.bmp");

        Bmp24Codec.Write(path, image);
        var loaded = Bmp24Codec.Read(path);

        Assert.Equal(1, loaded.Width);
        Assert.Equal(1, loaded.Height);
        Assert.Equal((byte)200, loaded.Pixels[0]);
        Assert.Equal((byte)100, loaded.Pixels[1]);
        Assert.Equal((byte)50, loaded.Pixels[2]);
    }

    [Fact]
    public void Bmp24Codec_NonSquare_RoundTrip()
    {
        var width = 11;
        var height = 5;
        var pixels = new byte[width * height * 3];
        var rng = new Random(55);
        rng.NextBytes(pixels);

        var image = new Bmp24Image(width, height, pixels);
        var path = Path.Combine(_tempDir, "nonsquare24.bmp");

        Bmp24Codec.Write(path, image);
        var loaded = Bmp24Codec.Read(path);

        Assert.Equal(width, loaded.Width);
        Assert.Equal(height, loaded.Height);
        Assert.Equal(pixels, loaded.Pixels);
    }

    [Fact]
    public void Bmp24Codec_LargerImage_PreservesAllPixels()
    {
        var width = 64;
        var height = 48;
        var pixels = new byte[width * height * 3];
        var rng = new Random(123);
        rng.NextBytes(pixels);

        var image = new Bmp24Image(width, height, pixels);
        var path = Path.Combine(_tempDir, "larger24.bmp");

        Bmp24Codec.Write(path, image);
        var loaded = Bmp24Codec.Read(path);

        Assert.Equal(width, loaded.Width);
        Assert.Equal(height, loaded.Height);
        Assert.Equal(pixels, loaded.Pixels);
    }

    // --- BmpCodec.TryReadInfo tests ---

    [Fact]
    public void BmpCodec_TryReadInfo_8bit()
    {
        var palette = Bmp8Codec.CreateGrayscalePalette();
        var image = new Bmp8Image(16, 8, new byte[16 * 8], palette);
        var path = Path.Combine(_tempDir, "info8.bmp");
        Bmp8Codec.Write(path, image);

        Assert.True(BmpCodec.TryReadInfo(path, out var width, out var height, out var bits));
        Assert.Equal(16, width);
        Assert.Equal(8, height);
        Assert.Equal(8, bits);
    }

    [Fact]
    public void BmpCodec_TryReadInfo_24bit()
    {
        var image = new Bmp24Image(20, 10, new byte[20 * 10 * 3]);
        var path = Path.Combine(_tempDir, "info24.bmp");
        Bmp24Codec.Write(path, image);

        Assert.True(BmpCodec.TryReadInfo(path, out var width, out var height, out var bits));
        Assert.Equal(20, width);
        Assert.Equal(10, height);
        Assert.Equal(24, bits);
    }

    [Fact]
    public void BmpCodec_TryReadInfo_MissingFile_ReturnsFalse()
    {
        Assert.False(BmpCodec.TryReadInfo(Path.Combine(_tempDir, "nope.bmp"), out _, out _, out _));
    }

    [Fact]
    public void BmpCodec_TryReadInfo_InvalidFile_ReturnsFalse()
    {
        var path = Path.Combine(_tempDir, "notabmp.bmp");
        File.WriteAllBytes(path, new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        Assert.False(BmpCodec.TryReadInfo(path, out _, out _, out _));
    }

    // --- PaletteUtils tests ---

    [Fact]
    public void PaletteUtils_ExactMatch_ReturnsCorrectIndex()
    {
        var palette = Bmp8Codec.CreateGrayscalePalette();
        for (byte i = 0; i < 255; i++)
        {
            var idx = PaletteUtils.FindNearestIndex(palette, i, i, i);
            Assert.Equal(i, idx);
        }
    }

    [Fact]
    public void PaletteUtils_NearestMatch_ChoosesClosest()
    {
        var palette = new BmpPaletteEntry[3];
        palette[0] = new BmpPaletteEntry(0, 0, 255, 0); // Pure red (BMP is BGR, but PaletteEntry stores B,G,R)
        palette[1] = new BmpPaletteEntry(0, 255, 0, 0); // Pure green
        palette[2] = new BmpPaletteEntry(255, 0, 0, 0); // Pure blue

        // The FindNearestIndex takes (r, g, b) and compares against entry.Red, entry.Green, entry.Blue
        // entry[0].Red=255, Green=0, Blue=0 -> this is red
        // entry[1].Red=0, Green=255, Blue=0 -> green
        // entry[2].Red=0, Green=0, Blue=255 -> blue

        // Searching for red (200, 0, 0) should find index 0
        var fullPalette = new BmpPaletteEntry[256];
        fullPalette[0] = new BmpPaletteEntry(0, 0, 255, 0); // Red=255
        fullPalette[1] = new BmpPaletteEntry(0, 255, 0, 0); // Green=255
        fullPalette[2] = new BmpPaletteEntry(255, 0, 0, 0); // Blue=255
        for (var i = 3; i < 256; i++)
        {
            fullPalette[i] = new BmpPaletteEntry(0, 0, 0, 0);
        }

        var idx = PaletteUtils.FindNearestIndex(fullPalette, 200, 10, 10);
        Assert.Equal((byte)0, idx);
    }

    [Fact]
    public void PaletteUtils_BlackPixel_MatchesBlackEntry()
    {
        var palette = Bmp8Codec.CreateGrayscalePalette();
        var idx = PaletteUtils.FindNearestIndex(palette, 0, 0, 0);
        Assert.Equal((byte)0, idx);
    }

    [Fact]
    public void PaletteUtils_WhitePixel_MatchesWhiteEntry()
    {
        var palette = Bmp8Codec.CreateGrayscalePalette();
        var idx = PaletteUtils.FindNearestIndex(palette, 255, 255, 255);
        Assert.Equal((byte)255, idx);
    }

    [Fact]
    public void PaletteUtils_MidGray_MatchesExpected()
    {
        var palette = Bmp8Codec.CreateGrayscalePalette();
        // For a grayscale palette, entry[128] = (128,128,128)
        var idx = PaletteUtils.FindNearestIndex(palette, 128, 128, 128);
        Assert.Equal((byte)128, idx);
    }
}
