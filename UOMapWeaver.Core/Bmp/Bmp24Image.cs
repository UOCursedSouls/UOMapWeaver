namespace UOMapWeaver.Core.Bmp;

public sealed class Bmp24Image
{
    public Bmp24Image(int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width and height must be positive.");
        }

        if (pixels.Length != width * height * 3)
        {
            throw new ArgumentException("Pixel buffer size does not match width/height.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }
}
