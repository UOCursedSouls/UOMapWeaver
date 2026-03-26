using System.Text.Json.Serialization;

namespace UOMapWeaver.Core.ResourcePack;

/// <summary>
/// Definition of a logical item (e.g., "stone_wall") that maps to multiple
/// physical tile IDs depending on direction/state/variety.
/// Like Minecraft's block state definitions, but for UO isometric items.
/// </summary>
public class ItemDefinition
{
    /// <summary>Unique identifier, e.g. "stone_wall", "wooden_door", "cedar_tree".</summary>
    public string Id { get; set; } = "";

    /// <summary>Category for filtering: Wall, Door, Vegetation, etc.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ItemCategory Category { get; set; }

    /// <summary>Human-readable name, e.g. "Stone Wall".</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Item height from tiledata.mul.</summary>
    public byte Height { get; set; }

    /// <summary>
    /// Directional variants: each direction maps to one or more tile IDs.
    /// Multiple IDs per direction = visual variety (random selection).
    /// Example: { "South": [200, 201], "East": [221, 222], "Corner": [204] }
    /// </summary>
    public Dictionary<string, ushort[]> Variants { get; set; } = new();

    /// <summary>
    /// For composite items (trees: trunk+canopy), defines the parts and their relative positions.
    /// Null for single-tile items.
    /// </summary>
    public MultiPartDef[]? MultiParts { get; set; }

    /// <summary>
    /// Confidence score (0.0-1.0) when learned from reference map.
    /// Null for hand-authored entries.
    /// </summary>
    public double? Confidence { get; set; }

    /// <summary>
    /// Optional tags for filtering: "material:stone", "style:british", "era:t2a".
    /// </summary>
    public string[]? Tags { get; set; }

    /// <summary>Get tile IDs for a specific direction.</summary>
    public ushort[] GetVariant(ItemDirection direction)
    {
        var key = direction.ToString();
        return Variants.TryGetValue(key, out var ids) ? ids : [];
    }

    /// <summary>Get all tile IDs across all variants.</summary>
    [JsonIgnore]
    public IEnumerable<ushort> AllTileIds => Variants.Values.SelectMany(v => v);
}

/// <summary>
/// A part of a multi-part item (e.g., tree trunk, tree canopy).
/// </summary>
public class MultiPartDef
{
    /// <summary>Part name: "trunk", "canopy", "base", etc.</summary>
    public string PartName { get; set; } = "";

    /// <summary>Tile ID for this part.</summary>
    public ushort TileId { get; set; }

    /// <summary>Relative X offset from the item's anchor position.</summary>
    public int DeltaX { get; set; }

    /// <summary>Relative Y offset.</summary>
    public int DeltaY { get; set; }

    /// <summary>Relative Z offset.</summary>
    public int DeltaZ { get; set; }
}

/// <summary>
/// A JSON file containing item definitions for one category.
/// </summary>
public class ItemRegistryFile
{
    /// <summary>Category name matching ItemCategory enum.</summary>
    public string Category { get; set; } = "";

    /// <summary>All item definitions in this file.</summary>
    public List<ItemDefinition> Items { get; set; } = new();
}
