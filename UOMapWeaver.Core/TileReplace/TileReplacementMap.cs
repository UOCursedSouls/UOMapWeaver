using System.Collections.Generic;

namespace UOMapWeaver.Core.TileReplace;

public sealed class TileReplacementMap
{
    public TileReplacementMap(
        Dictionary<ushort, ushort>? terrain = null,
        Dictionary<ushort, ushort>? statics = null)
    {
        Terrain = terrain ?? new Dictionary<ushort, ushort>();
        Statics = statics ?? new Dictionary<ushort, ushort>();
    }

    public Dictionary<ushort, ushort> Terrain { get; }

    public Dictionary<ushort, ushort> Statics { get; }

    public string? SourceClientPath { get; set; }

    public string? DestClientPath { get; set; }

    public bool TryGetTerrainReplacement(ushort tileId, out ushort replacement)
        => Terrain.TryGetValue(tileId, out replacement);

    public bool TryGetStaticReplacement(ushort tileId, out ushort replacement)
        => Statics.TryGetValue(tileId, out replacement);
}
