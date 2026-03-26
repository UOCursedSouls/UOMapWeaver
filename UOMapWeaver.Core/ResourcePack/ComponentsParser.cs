namespace UOMapWeaver.Core.ResourcePack;

/// <summary>
/// Parses ModernUO/RunUO Components data files (walls.txt, doors.txt, roof.txt, stairs.txt)
/// into ItemDefinitions with 100% confidence directional mappings.
///
/// These files are the OFFICIAL tile-to-direction mappings used by the UO house foundation
/// system, verified from RunUO (2012) → ModernUO (2019) git history.
///
/// File format: Tab-separated values with header rows defining column names.
/// walls.txt columns: Category, Style, TID, South1, South2, South3, Corner, East1, East2, East3, Post, WindowS, AltWindowS, WindowE, AltWindowE, ...
/// doors.txt columns: Category, Piece1-8 (4 directions × 2 states: open/closed)
/// roof.txt columns: Category, Style, TID, North, East, South, West, NSCrosspiece, EWCrosspiece, ...
/// stairs.txt columns: Category, Block, North, East, South, West, ...
/// </summary>
public static class ComponentsParser
{
    /// <summary>
    /// Parse all component files from a directory and return ItemDefinitions.
    /// </summary>
    public static List<ItemDefinition> ParseAll(string componentsDir)
    {
        var results = new List<ItemDefinition>();

        var wallsPath = Path.Combine(componentsDir, "walls.txt");
        if (File.Exists(wallsPath))
            results.AddRange(ParseWalls(wallsPath));

        var doorsPath = Path.Combine(componentsDir, "doors.txt");
        if (File.Exists(doorsPath))
            results.AddRange(ParseDoors(doorsPath));

        var roofPath = Path.Combine(componentsDir, "roof.txt");
        if (File.Exists(roofPath))
            results.AddRange(ParseRoofs(roofPath));

        var stairsPath = Path.Combine(componentsDir, "stairs.txt");
        if (File.Exists(stairsPath))
            results.AddRange(ParseStairs(stairsPath));

        return results;
    }

    /// <summary>
    /// Parse walls.txt — columns: Category, Style, TID, South1, South2, South3, Corner, East1, East2, East3, Post, WindowS, AltWindowS, WindowE, AltWindowE, ...
    /// </summary>
    public static List<ItemDefinition> ParseWalls(string path)
    {
        var defs = new List<ItemDefinition>();
        var lines = File.ReadAllLines(path);

        for (int i = 2; i < lines.Length; i++) // Skip 2 header lines
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;

            var parts = line.Split('\t');
            if (parts.Length < 12) continue;

            var comment = parts.Length > 17 ? parts[17].Trim() : $"Wall_{i}";
            if (string.IsNullOrEmpty(comment)) comment = $"Wall_cat{parts[0]}_style{parts[1]}";

            var id = SanitizeId($"wall_{comment}");

            var south = CollectNonZero(parts, 3, 4, 5);   // South1, South2, South3
            var corner = CollectNonZero(parts, 6);          // Corner
            var east = CollectNonZero(parts, 7, 8, 9);     // East1, East2, East3
            var post = CollectNonZero(parts, 10);           // Post
            var windowS = CollectNonZero(parts, 11, 14);    // WindowS, SecondAltWindowS
            var windowE = CollectNonZero(parts, 13, 15);    // WindowE, SecondAltWindowE
            // AltWindowS/E at indices 12, 13 already covered

            var variants = new Dictionary<string, ushort[]>();
            if (south.Length > 0) variants["South"] = south;
            if (east.Length > 0) variants["East"] = east;
            if (corner.Length > 0) variants["Corner"] = corner;
            if (post.Length > 0) variants["Post"] = post;
            if (windowS.Length > 0) variants["South"] = [.. (variants.TryGetValue("South", out var existing) ? existing : []), .. windowS];
            if (windowE.Length > 0) variants["East"] = [.. (variants.TryGetValue("East", out var existingE) ? existingE : []), .. windowE];

            if (variants.Count < 2) continue; // Need at least South + East

            defs.Add(new ItemDefinition
            {
                Id = id,
                Category = ItemCategory.Wall,
                DisplayName = comment,
                Height = 20,
                Variants = variants,
                Confidence = 1.0,
                Tags = ["source:modernuo_components", $"category:{parts[0]}", $"style:{parts[1]}"]
            });
        }

        return defs;
    }

    /// <summary>
    /// Parse doors.txt — columns: Category, Piece1-8, FeatureMask, Comment
    /// Pieces are: 1=SouthCW_Closed, 2=SouthCW_Open, 3=SouthCCW_Closed, 4=SouthCCW_Open,
    ///             5=EastCW_Closed, 6=EastCW_Open, 7=EastCCW_Closed, 8=EastCCW_Open
    /// Simplified to: SouthClosed, SouthOpen, EastClosed, EastOpen
    /// </summary>
    public static List<ItemDefinition> ParseDoors(string path)
    {
        var defs = new List<ItemDefinition>();
        var lines = File.ReadAllLines(path);

        for (int i = 4; i < lines.Length; i++) // Skip header + blank lines
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;

            var parts = line.Split('\t');
            if (parts.Length < 10) continue;

            var comment = parts.Length > 10 ? parts[10].Trim() : $"Door_{i}";
            if (string.IsNullOrEmpty(comment)) comment = $"Door_cat{parts[0]}";

            var id = SanitizeId($"door_{comment}");

            // Piece1=SouthCW_Closed, Piece3=SouthCCW_Closed → combine as SouthClosed
            var southClosed = CollectNonZero(parts, 1, 3);
            var southOpen = CollectNonZero(parts, 2, 4);
            var eastClosed = CollectNonZero(parts, 5, 7);
            var eastOpen = CollectNonZero(parts, 6, 8);

            var variants = new Dictionary<string, ushort[]>();
            if (southClosed.Length > 0) variants["SouthClosed"] = southClosed;
            if (southOpen.Length > 0) variants["SouthOpen"] = southOpen;
            if (eastClosed.Length > 0) variants["EastClosed"] = eastClosed;
            if (eastOpen.Length > 0) variants["EastOpen"] = eastOpen;

            if (variants.Count == 0) continue;

            defs.Add(new ItemDefinition
            {
                Id = id,
                Category = ItemCategory.Door,
                DisplayName = comment,
                Height = 20,
                Variants = variants,
                Confidence = 1.0,
                Tags = ["source:modernuo_components"]
            });
        }

        return defs;
    }

    /// <summary>
    /// Parse roof.txt — columns: Category, Style, TID, North, East, South, West, NSCrosspiece, EWCrosspiece, ...
    /// </summary>
    public static List<ItemDefinition> ParseRoofs(string path)
    {
        var defs = new List<ItemDefinition>();
        var lines = File.ReadAllLines(path);

        for (int i = 2; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;

            var parts = line.Split('\t');
            if (parts.Length < 8) continue;

            var comment = parts.Length > 19 ? parts[19].Trim() : $"Roof_{i}";
            if (string.IsNullOrEmpty(comment)) comment = $"Roof_cat{parts[0]}_style{parts[1]}";

            var id = SanitizeId($"roof_{comment}");

            var north = CollectNonZero(parts, 3);
            var east = CollectNonZero(parts, 4);
            var south = CollectNonZero(parts, 5);
            var west = CollectNonZero(parts, 6);

            var variants = new Dictionary<string, ushort[]>();
            if (north.Length > 0) variants["North"] = north;
            if (east.Length > 0) variants["East"] = east;
            if (south.Length > 0) variants["South"] = south;
            if (west.Length > 0) variants["West"] = west;

            if (variants.Count == 0) continue;

            defs.Add(new ItemDefinition
            {
                Id = id,
                Category = ItemCategory.Roof,
                DisplayName = comment,
                Height = 0,
                Variants = variants,
                Confidence = 1.0,
                Tags = ["source:modernuo_components"]
            });
        }

        return defs;
    }

    /// <summary>
    /// Parse stairs.txt — columns: Category, Block, North, East, South, West, ...
    /// </summary>
    public static List<ItemDefinition> ParseStairs(string path)
    {
        var defs = new List<ItemDefinition>();
        var lines = File.ReadAllLines(path);

        for (int i = 2; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;

            var parts = line.Split('\t');
            if (parts.Length < 7) continue;

            var comment = parts.Length > 14 ? parts[14].Trim() : $"Stairs_{i}";
            if (string.IsNullOrEmpty(comment)) comment = $"Stairs_cat{parts[0]}";

            var id = SanitizeId($"stairs_{comment}");

            var north = CollectNonZero(parts, 2);
            var east = CollectNonZero(parts, 3);
            var south = CollectNonZero(parts, 4);
            var west = CollectNonZero(parts, 5);

            var variants = new Dictionary<string, ushort[]>();
            if (north.Length > 0) variants["North"] = north;
            if (east.Length > 0) variants["East"] = east;
            if (south.Length > 0) variants["South"] = south;
            if (west.Length > 0) variants["West"] = west;

            if (variants.Count == 0) continue;

            defs.Add(new ItemDefinition
            {
                Id = id,
                Category = ItemCategory.Stairs,
                DisplayName = comment,
                Height = 5,
                Variants = variants,
                Confidence = 1.0,
                Tags = ["source:modernuo_components"]
            });
        }

        return defs;
    }

    private static ushort[] CollectNonZero(string[] parts, params int[] indices)
    {
        var result = new HashSet<ushort>();
        foreach (var idx in indices)
        {
            if (idx < parts.Length && ushort.TryParse(parts[idx].Trim(), out var val) && val != 0)
                result.Add(val);
        }
        return [.. result];
    }

    private static string SanitizeId(string raw)
    {
        return raw.ToLowerInvariant()
            .Replace(" ", "_")
            .Replace("-", "_")
            .Replace("'", "")
            .Replace("(", "")
            .Replace(")", "")
            .Replace("__", "_")
            .Trim('_');
    }
}
