namespace UOMapWeaver.Core.Map;

public readonly struct LandTile
{
    public LandTile(ushort tileId, sbyte z)
    {
        TileId = tileId;
        Z = z;
    }

    public ushort TileId { get; }

    public sbyte Z { get; }
}

