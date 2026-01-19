namespace UOMapWeaver.Core.Statics;

public sealed record StaticPlacementDefinition(string Name, int Chance, IReadOnlyList<StaticPlacementGroup> Groups);

public sealed record StaticPlacementGroup(int Weight, IReadOnlyList<StaticPlacementItem> Items);

public readonly record struct StaticPlacementItem(ushort TileId, int X, int Y, sbyte Z, ushort Hue);

public sealed record TerrainDefinition(string Name, ushort TileId, bool Random);
