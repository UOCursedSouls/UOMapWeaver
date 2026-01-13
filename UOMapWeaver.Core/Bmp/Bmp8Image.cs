namespace UOMapWeaver.Core.Bmp;

public sealed class Bmp8Image
{
    public Bmp8Image(int width, int height, byte[] pixels, BmpPaletteEntry[] palette)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
        Palette = palette;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    public BmpPaletteEntry[] Palette { get; }
}

public readonly struct BmpPaletteEntry
{
    public BmpPaletteEntry(byte blue, byte green, byte red, byte alpha)
    {
        Blue = blue;
        Green = green;
        Red = red;
        Alpha = alpha;
    }

    public byte Blue { get; }

    public byte Green { get; }

    public byte Red { get; }

    public byte Alpha { get; }
}

