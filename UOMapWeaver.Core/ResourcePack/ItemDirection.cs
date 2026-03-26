namespace UOMapWeaver.Core.ResourcePack;

/// <summary>
/// Directional variant of a static item. In UO, each physical direction
/// has a separate tile ID with its own sprite.
/// For walls: South, East, Corner, Post.
/// For doors: SouthClosed, EastClosed, SouthOpen, EastOpen.
/// For non-directional items: None.
/// </summary>
public enum ItemDirection
{
    /// <summary>Wall/item facing south (visible from south side).</summary>
    South,
    /// <summary>Wall/item facing east (visible from east side).</summary>
    East,
    /// <summary>Corner piece connecting south and east walls.</summary>
    Corner,
    /// <summary>Post/pillar at wall endpoint or intersection.</summary>
    Post,
    /// <summary>Door facing south, closed state.</summary>
    SouthClosed,
    /// <summary>Door facing east, closed state.</summary>
    EastClosed,
    /// <summary>Door facing south, open state.</summary>
    SouthOpen,
    /// <summary>Door facing east, open state.</summary>
    EastOpen,
    /// <summary>Wall/item facing north (rare in UO).</summary>
    North,
    /// <summary>Wall/item facing west (rare in UO).</summary>
    West,
    /// <summary>Non-directional item (furniture, vegetation, etc.).</summary>
    None
}
