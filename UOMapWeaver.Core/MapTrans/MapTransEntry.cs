namespace UOMapWeaver.Core.MapTrans;

public sealed class MapTransEntry
{
    public MapTransEntry(byte colorIndex, int altitude, IReadOnlyList<ushort> tileIds, byte? group = null)
    {
        ColorIndex = colorIndex;
        Altitude = altitude;
        TileIds = tileIds;
        Group = group;
    }

    public byte ColorIndex { get; }

    public int Altitude { get; }

    public IReadOnlyList<ushort> TileIds { get; }

    public byte? Group { get; }
}

