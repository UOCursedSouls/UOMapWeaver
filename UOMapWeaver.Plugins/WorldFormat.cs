// SPDX-License-Identifier: GPL-3.0-only
// Plugin contract of UOMapWeaver (ADR-0003 section 2). Any change to this public surface bumps WorldFormatContract.Version.
namespace UOMapWeaver.Plugins;

public static class WorldFormatContract
{
    public const int Version = 1;
}

[Flags]
public enum WorldFormatCapabilities
{
    None = 0,
    Import = 1,
    Export = 2
}

public interface IWorldFormatPlugin
{
    /// <summary>The <see cref="WorldFormatContract.Version"/> the plugin was built against.</summary>
    int ContractVersion { get; }

    /// <summary>Stable, lower-case identifier, e.g. "mondaingem".</summary>
    string Id { get; }

    /// <summary>Name shown in the UI.</summary>
    string DisplayName { get; }

    WorldFormatCapabilities Capabilities { get; }

    IWorldWriter CreateWorld(string worldFolder, WorldCreateOptions options);

    IWorldReader OpenWorld(string worldFolder);
}

public sealed record WorldCreateOptions(string WorldName, int WidthInBlocks, int HeightInBlocks);

/// <summary>0 x 0 means an unbounded world.</summary>
public sealed record WorldInfo(string WorldName, int WidthInBlocks, int HeightInBlocks);

public interface IWorldWriter : IDisposable
{
    /// <summary>Any order, each block at most once.</summary>
    void WriteBlock(WorldBlock block);

    /// <summary>Flushes. A writer disposed without Complete leaves no valid world.</summary>
    void Complete();
}

public interface IWorldReader : IDisposable
{
    WorldInfo Info { get; }

    /// <summary>False when there is no block at that position.</summary>
    bool TryReadBlock(int blockX, int blockY, out WorldBlock? block);
}

public sealed class WorldBlock
{
    public const int Size = 8;

    public WorldBlock(int blockX, int blockY, WorldLandTile[] land, IReadOnlyList<WorldStaticTile> statics)
    {
        ArgumentNullException.ThrowIfNull(land);
        ArgumentNullException.ThrowIfNull(statics);

        if (land.Length != Size * Size)
        {
            throw new ArgumentException($"A block has exactly {Size * Size} land tiles, got {land.Length}.", nameof(land));
        }

        foreach (var s in statics)
        {
            if (s.X >= Size || s.Y >= Size)
            {
                throw new ArgumentException($"Static position ({s.X},{s.Y}) is outside the {Size}x{Size} block.", nameof(statics));
            }
        }

        BlockX = blockX;
        BlockY = blockY;
        Land = land;
        Statics = statics;
    }

    /// <summary>World block coordinates, (0,0) = top-left of the world.</summary>
    public int BlockX { get; }

    public int BlockY { get; }

    /// <summary>Exactly 64, index y * 8 + x (UO map.mul block order).</summary>
    public WorldLandTile[] Land { get; }

    /// <summary>In file order, X and Y 0-7 inside the block.</summary>
    public IReadOnlyList<WorldStaticTile> Statics { get; }
}

public readonly record struct WorldLandTile(ushort TileId, sbyte Z);

public readonly record struct WorldStaticTile(ushort TileId, byte X, byte Y, sbyte Z, ushort Hue);
