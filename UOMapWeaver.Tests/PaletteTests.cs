using System;
using System.IO;
using UOMapWeaver.Core.Bmp;

namespace UOMapWeaver.Tests;

public sealed class PaletteTests
{
    [Fact]
    public void PaletteBmp_ReadWrite_RetainsPaletteLength()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "UOMapWeaver.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var palettePath = Path.Combine(tempDir, "palette.bmp");
            var palette = Bmp8Codec.CreateGrayscalePalette();
            var image = new Bmp8Image(1, 1, new byte[] { 0 }, palette);
            Bmp8Codec.Write(palettePath, image);

            var read = Bmp8Codec.Read(palettePath);
            Assert.Equal(256, read.Palette.Length);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void PaletteUtils_FindNearestIndex_UsesExpectedGrayscale()
    {
        var palette = Bmp8Codec.CreateGrayscalePalette();
        var index = PaletteUtils.FindNearestIndex(palette, 120, 120, 120);
        Assert.Equal((byte)120, index);
    }
}
