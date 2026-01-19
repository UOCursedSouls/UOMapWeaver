using System;
using System.IO;
using System.Linq;
using UOMapWeaver.Core;
using UOMapWeaver.Core.Bmp;

namespace UOMapWeaver.Tests;

public sealed class PaletteQuickSelectTests
{
    [Fact]
    public void QuickSelectPaletteFiles_AreValidBmp8()
    {
        var root = FindDataRoot();
        if (root is null)
        {
            return;
        }

        var candidates = new[]
        {
            UOMapWeaverDataPaths.PalettesRoot,
            UOMapWeaverDataPaths.SystemRoot,
            UOMapWeaverDataPaths.PhotoshopRoot
        };

        var paletteFiles = candidates
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.bmp", SearchOption.AllDirectories))
            .Where(path =>
                path.EndsWith("TerrainPalette.bmp", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("Palette.bmp", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (paletteFiles.Count == 0)
        {
            return;
        }

        foreach (var file in paletteFiles)
        {
            Assert.True(BmpCodec.TryReadInfo(file, out _, out _, out var bits), $"Unable to read BMP info for {file}");
            Assert.Equal(8, bits);
            var bmp = Bmp8Codec.Read(file);
            Assert.Equal(256, bmp.Palette.Length);
        }
    }

    private static string? FindDataRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir != null; i++)
        {
            var candidate = Path.Combine(dir.FullName, UOMapWeaverDataPaths.DataFolderName);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
