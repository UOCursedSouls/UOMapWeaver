namespace UOMapWeaver.Core.TileColors;

public readonly struct RgbColor
{
    public RgbColor(byte r, byte g, byte b)
    {
        R = r;
        G = g;
        B = b;
    }

    public byte R { get; }

    public byte G { get; }

    public byte B { get; }

    public int Key => (R << 16) | (G << 8) | B;

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public static bool TryParse(string value, out RgbColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text.StartsWith("#", StringComparison.Ordinal))
        {
            text = text[1..];
        }

        if (text.Length != 6)
        {
            return false;
        }

        if (!byte.TryParse(text[..2], System.Globalization.NumberStyles.HexNumber, null, out var r))
        {
            return false;
        }

        if (!byte.TryParse(text.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g))
        {
            return false;
        }

        if (!byte.TryParse(text.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return false;
        }

        color = new RgbColor(r, g, b);
        return true;
    }
}
