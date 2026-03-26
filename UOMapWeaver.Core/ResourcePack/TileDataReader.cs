using System.Text;

namespace UOMapWeaver.Core.ResourcePack;

/// <summary>
/// Tile flags from tiledata.mul. Each flag describes a property of a land or item tile.
/// Uses ulong to support both classic (32-bit) and High Seas (64-bit) flag fields.
/// </summary>
[Flags]
public enum TileFlag : ulong
{
    None = 0,
    Background = 1UL << 0,
    Weapon = 1UL << 1,
    Transparent = 1UL << 2,
    Translucent = 1UL << 3,
    Wall = 1UL << 4,
    Damaging = 1UL << 5,
    Impassable = 1UL << 6,
    Wet = 1UL << 7,
    Surface = 1UL << 9,
    Bridge = 1UL << 10,
    Stackable = 1UL << 11,
    Window = 1UL << 12,
    NoShoot = 1UL << 13,
    Foliage = 1UL << 16,
    PartialHue = 1UL << 17,
    Map = 1UL << 19,
    Container = 1UL << 21,
    Wearable = 1UL << 22,
    LightSource = 1UL << 23,
    Roof = 1UL << 26,
    Door = 1UL << 27,
    StairBack = 1UL << 28,
    StairRight = 1UL << 29,
}

/// <summary>
/// Data for a single land tile from tiledata.mul.
/// </summary>
public record LandTileData(ushort TileId, TileFlag Flags, ushort TextureId, string Name);

/// <summary>
/// Data for a single item/static tile from tiledata.mul.
/// </summary>
public record ItemTileData(ushort TileId, TileFlag Flags, byte Weight, byte Height, string Name);

/// <summary>
/// A set of wall tile IDs that form a visual group (south, east, corner, post, optional doors).
/// </summary>
public record WallSet(
    string Name,
    ushort SouthWall,
    ushort EastWall,
    ushort Corner,
    ushort Post,
    ushort? DoorSouth,
    ushort? DoorEast
);

/// <summary>
/// Reads tiledata.mul from a UO client installation.
/// Auto-detects classic vs High Seas (HS) format:
///
/// Classic format:
/// - Land entry (26 bytes): uint32 flags, ushort textureId, 20 bytes name
/// - Item entry (37 bytes): uint32 flags, byte weight, byte quality, ushort unknown,
///   byte unknown2, byte quantity, ushort animId, byte unknown3, byte hue,
///   ushort unknown4, byte height, 20 bytes name
///
/// HS format (8-byte flags):
/// - Land entry (30 bytes): uint64 flags, ushort textureId, 20 bytes name
/// - Item entry (41 bytes): uint64 flags, byte weight, byte quality, ushort unknown,
///   byte unknown2, byte quantity, ushort animId, byte unknown3, byte hue,
///   ushort unknown4, byte height, 20 bytes name
///
/// Both: 512 groups of 32 land tiles + N groups of 32 item tiles, each group has 4-byte header.
/// </summary>
public class TileDataReader
{
    private const int LandGroupCount = 512;
    private const int TilesPerGroup = 32;
    private const int LandTileCount = LandGroupCount * TilesPerGroup; // 16384
    private const int GroupHeaderSize = 4;
    private const int NameLength = 20;

    // Classic format sizes
    private const int ClassicLandEntrySize = 26; // 4 flags + 2 texture + 20 name
    private const int ClassicItemEntrySize = 37; // 4 flags + 13 fields + 20 name

    // HS format sizes
    private const int HSLandEntrySize = 30; // 8 flags + 2 texture + 20 name
    private const int HSItemEntrySize = 41; // 8 flags + 13 fields + 20 name

    /// <summary>
    /// All land tile data entries (16384 entries, indexed by land tile ID).
    /// </summary>
    public LandTileData[] LandTiles { get; }

    /// <summary>
    /// All item/static tile data entries (indexed by item tile ID).
    /// </summary>
    public ItemTileData[] ItemTiles { get; }

    /// <summary>
    /// Whether the file uses the High Seas format (64-bit flags).
    /// </summary>
    public bool IsHighSeas { get; }

    /// <summary>
    /// Read tiledata.mul from the specified path.
    /// </summary>
    public TileDataReader(string tiledataPath)
    {
        if (!File.Exists(tiledataPath))
        {
            throw new FileNotFoundException("tiledata.mul not found", tiledataPath);
        }

        using var stream = File.OpenRead(tiledataPath);
        using var reader = new BinaryReader(stream);

        // Auto-detect format by checking if HS item groups divide evenly
        IsHighSeas = DetectFormat(stream.Length);

        var landEntrySize = IsHighSeas ? HSLandEntrySize : ClassicLandEntrySize;
        var itemEntrySize = IsHighSeas ? HSItemEntrySize : ClassicItemEntrySize;

        // Read land tiles: 512 groups of 32 entries
        LandTiles = new LandTileData[LandTileCount];
        for (int group = 0; group < LandGroupCount; group++)
        {
            reader.ReadInt32(); // Group header (skip)

            for (int i = 0; i < TilesPerGroup; i++)
            {
                var tileId = (ushort)(group * TilesPerGroup + i);
                var flags = ReadFlags(reader);
                var textureId = reader.ReadUInt16();
                var name = ReadName(reader);

                LandTiles[tileId] = new LandTileData(tileId, flags, textureId, name);
            }
        }

        // Read item tiles: remaining data in groups of 32
        var remainingBytes = stream.Length - stream.Position;
        var bytesPerItemGroup = GroupHeaderSize + TilesPerGroup * itemEntrySize;
        var itemGroupCount = (int)(remainingBytes / bytesPerItemGroup);
        var itemTileCount = itemGroupCount * TilesPerGroup;

        ItemTiles = new ItemTileData[itemTileCount];
        for (int group = 0; group < itemGroupCount; group++)
        {
            reader.ReadInt32(); // Group header (skip)

            for (int i = 0; i < TilesPerGroup; i++)
            {
                var tileId = (ushort)(group * TilesPerGroup + i);
                var flags = ReadFlags(reader);
                var weight = reader.ReadByte();
                var quality = reader.ReadByte();
                var unknown = reader.ReadUInt16();
                var unknown2 = reader.ReadByte();
                var quantity = reader.ReadByte();
                var animId = reader.ReadUInt16();
                var unknown3 = reader.ReadByte();
                var hue = reader.ReadByte();
                var unknown4 = reader.ReadUInt16();
                var height = reader.ReadByte();
                var name = ReadName(reader);

                if (tileId < ItemTiles.Length)
                {
                    ItemTiles[tileId] = new ItemTileData(tileId, flags, weight, height, name);
                }
            }
        }
    }

    /// <summary>
    /// Detect whether the file uses HS format by checking if item groups divide evenly
    /// with 41-byte entries (HS) vs 37-byte entries (classic).
    /// </summary>
    private static bool DetectFormat(long fileSize)
    {
        var hsLandSize = (long)LandGroupCount * (GroupHeaderSize + TilesPerGroup * HSLandEntrySize);
        var classicLandSize = (long)LandGroupCount * (GroupHeaderSize + TilesPerGroup * ClassicLandEntrySize);

        var hsItemBytes = fileSize - hsLandSize;
        var classicItemBytes = fileSize - classicLandSize;

        var hsBytesPerItemGroup = GroupHeaderSize + TilesPerGroup * HSItemEntrySize;
        var classicBytesPerItemGroup = GroupHeaderSize + TilesPerGroup * ClassicItemEntrySize;

        var hsClean = hsItemBytes > 0 && hsItemBytes % hsBytesPerItemGroup == 0;
        var classicClean = classicItemBytes > 0 && classicItemBytes % classicBytesPerItemGroup == 0;

        // If only one format gives a clean division, use it
        if (hsClean && !classicClean)
        {
            return true;
        }

        if (classicClean && !hsClean)
        {
            return false;
        }

        // Both clean (or neither) — prefer HS as it's more common in modern clients
        return hsClean;
    }

    /// <summary>
    /// Read flags field (4 or 8 bytes depending on format).
    /// </summary>
    private TileFlag ReadFlags(BinaryReader reader)
    {
        if (IsHighSeas)
        {
            return (TileFlag)reader.ReadUInt64();
        }

        return (TileFlag)reader.ReadUInt32();
    }

    /// <summary>
    /// Check if an item tile has the Wall flag.
    /// </summary>
    public bool IsWall(ushort itemTileId) =>
        itemTileId < ItemTiles.Length && ItemTiles[itemTileId] != null &&
        (ItemTiles[itemTileId].Flags & TileFlag.Wall) != 0;

    /// <summary>
    /// Check if an item tile has the Door flag.
    /// </summary>
    public bool IsDoor(ushort itemTileId) =>
        itemTileId < ItemTiles.Length && ItemTiles[itemTileId] != null &&
        (ItemTiles[itemTileId].Flags & TileFlag.Door) != 0;

    /// <summary>
    /// Check if an item tile has the Roof flag.
    /// </summary>
    public bool IsRoof(ushort itemTileId) =>
        itemTileId < ItemTiles.Length && ItemTiles[itemTileId] != null &&
        (ItemTiles[itemTileId].Flags & TileFlag.Roof) != 0;

    /// <summary>
    /// Check if an item tile has the Surface flag.
    /// </summary>
    public bool IsSurface(ushort itemTileId) =>
        itemTileId < ItemTiles.Length && ItemTiles[itemTileId] != null &&
        (ItemTiles[itemTileId].Flags & TileFlag.Surface) != 0;

    /// <summary>
    /// Check if an item tile has the Impassable flag.
    /// </summary>
    public bool IsImpassable(ushort itemTileId) =>
        itemTileId < ItemTiles.Length && ItemTiles[itemTileId] != null &&
        (ItemTiles[itemTileId].Flags & TileFlag.Impassable) != 0;

    /// <summary>
    /// Check if a land tile has the Wet flag (water).
    /// </summary>
    public bool IsWater(ushort landTileId) =>
        landTileId < LandTiles.Length && LandTiles[landTileId] != null &&
        (LandTiles[landTileId].Flags & TileFlag.Wet) != 0;

    /// <summary>
    /// Group wall tiles by visual style. Walls typically come in consecutive IDs
    /// forming sets of south-facing, east-facing, corner, and post variants.
    /// Scans for clusters of 4+ consecutive Wall-flagged tiles that share a name prefix.
    /// </summary>
    public List<WallSet> GetWallSets()
    {
        var wallSets = new List<WallSet>();
        var i = 0;

        while (i < ItemTiles.Length - 3)
        {
            // Look for runs of Wall-flagged tiles with the same name root
            if (!IsWall((ushort)i))
            {
                i++;
                continue;
            }

            var startId = (ushort)i;
            var baseName = GetNameRoot(ItemTiles[i].Name);

            // Collect consecutive wall tiles with the same base name
            var runLength = 0;
            while (i + runLength < ItemTiles.Length &&
                   IsWall((ushort)(i + runLength)) &&
                   GetNameRoot(ItemTiles[i + runLength].Name) == baseName)
            {
                runLength++;
            }

            // A wall set needs at least 4 tiles (south, east, corner, post)
            if (runLength >= 4)
            {
                // Find associated doors nearby
                ushort? doorSouth = null;
                ushort? doorEast = null;

                // Search a small range after the wall run for doors
                for (int d = i + runLength; d < Math.Min(i + runLength + 10, ItemTiles.Length); d++)
                {
                    if (IsDoor((ushort)d))
                    {
                        if (doorSouth == null)
                        {
                            doorSouth = (ushort)d;
                        }
                        else if (doorEast == null)
                        {
                            doorEast = (ushort)d;
                            break;
                        }
                    }
                }

                wallSets.Add(new WallSet(
                    baseName,
                    startId,
                    (ushort)(startId + 1),
                    (ushort)(startId + 2),
                    (ushort)(startId + 3),
                    doorSouth,
                    doorEast
                ));
            }

            i += Math.Max(runLength, 1);
        }

        return wallSets;
    }

    /// <summary>
    /// Extract the root name from a tile name (remove trailing numbers and spaces).
    /// </summary>
    private static string GetNameRoot(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        var trimmed = name.TrimEnd();
        // Remove trailing digits and spaces to get the base name
        var end = trimmed.Length;
        while (end > 0 && (char.IsDigit(trimmed[end - 1]) || trimmed[end - 1] == ' '))
        {
            end--;
        }

        return end > 0 ? trimmed[..end].Trim() : trimmed;
    }

    /// <summary>
    /// Read a 20-byte null-terminated ASCII name from the stream.
    /// </summary>
    private static string ReadName(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(NameLength);
        var nullIndex = Array.IndexOf(bytes, (byte)0);
        var length = nullIndex >= 0 ? nullIndex : NameLength;
        return Encoding.ASCII.GetString(bytes, 0, length).Trim();
    }
}
