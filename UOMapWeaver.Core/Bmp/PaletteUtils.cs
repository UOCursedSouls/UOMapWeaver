namespace UOMapWeaver.Core.Bmp;

public static class PaletteUtils
{
    public static byte FindNearestIndex(BmpPaletteEntry[] palette, byte r, byte g, byte b)
    {
        var bestIndex = 0;
        var bestDistance = int.MaxValue;

        for (var i = 0; i < palette.Length; i++)
        {
            var entry = palette[i];
            var dr = entry.Red - r;
            var dg = entry.Green - g;
            var db = entry.Blue - b;
            var distance = dr * dr + dg * dg + db * db;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }

        return (byte)bestIndex;
    }
}
