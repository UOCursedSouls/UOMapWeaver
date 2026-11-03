namespace UOMapWeaver.Core.Bmp;

public sealed class Bmp8Image
{
    public Bmp8Image(int width, int height, byte[] pixels, BmpPaletteEntry[] palette)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width and height must be positive.");
        }

        if (pixels.Length != width * height)
        {
            throw new ArgumentException(
                $"Pixel buffer size ({pixels.Length}) does not match width*height ({width * height}).",
                nameof(pixels));
        }

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

