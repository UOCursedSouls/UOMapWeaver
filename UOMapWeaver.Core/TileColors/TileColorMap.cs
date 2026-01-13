using UOMapWeaver.Core.Bmp;

namespace UOMapWeaver.Core.TileColors;

public sealed class TileColorMap
{
    public TileColorMap(
        TileColorMode mode,
        Dictionary<ushort, byte> tileToIndex,
        Dictionary<ushort, RgbColor> tileToColor,
        Dictionary<byte, ushort> indexToTile,
        Dictionary<int, ushort> colorToTile,
        BmpPaletteEntry[]? palette,
        RgbColor unknownColor
    )
    {
        Mode = mode;
        TileToIndex = tileToIndex;
        TileToColor = tileToColor;
        IndexToTile = indexToTile;
        ColorToTile = colorToTile;
        Palette = palette;
        UnknownColor = unknownColor;
    }

    public TileColorMode Mode { get; }

    public Dictionary<ushort, byte> TileToIndex { get; }

    public Dictionary<ushort, RgbColor> TileToColor { get; }

    public Dictionary<byte, ushort> IndexToTile { get; }

    public Dictionary<int, ushort> ColorToTile { get; }

    public BmpPaletteEntry[]? Palette { get; }

    public RgbColor UnknownColor { get; }

    public bool TryGetColorIndex(ushort tileId, out byte index)
        => TileToIndex.TryGetValue(tileId, out index);

    public bool TryGetColor(ushort tileId, out RgbColor color)
        => TileToColor.TryGetValue(tileId, out color);

    public bool TryGetTileId(byte index, out ushort tileId)
        => IndexToTile.TryGetValue(index, out tileId);

    public bool TryGetTileId(RgbColor color, out ushort tileId)
        => ColorToTile.TryGetValue(color.Key, out tileId);
}
