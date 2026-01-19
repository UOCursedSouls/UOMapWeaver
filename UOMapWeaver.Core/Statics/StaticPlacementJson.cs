using System.Text.Json;

namespace UOMapWeaver.Core.Statics;

public static class StaticPlacementJson
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static List<TerrainDefinitionRecord> LoadTerrainRecords(string path)
    {
        if (!File.Exists(path))
        {
            return new List<TerrainDefinitionRecord>();
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<TerrainDefinitionRecord>>(json, JsonOptions) ??
                   new List<TerrainDefinitionRecord>();
        }
        catch
        {
            return new List<TerrainDefinitionRecord>();
        }
    }

    public static void SaveTerrainRecords(string path, IReadOnlyList<TerrainDefinitionRecord> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var json = JsonSerializer.Serialize(records, JsonOptions);
        File.WriteAllText(path, json);
    }

    public static List<TerrainDefinition> LoadTerrainDefinitions(string path)
    {
        var records = LoadTerrainRecords(path);
        return records
            .Where(record => !string.IsNullOrWhiteSpace(record.Name))
            .Select(record => new TerrainDefinition(record.Name!.Trim(), record.TileId, record.Random == true))
            .ToList();
    }

    public static StaticPlacementDefinition? LoadStaticDefinition(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            var record = JsonSerializer.Deserialize<StaticPlacementDefinitionRecord>(json, JsonOptions);
            if (record is null)
            {
                return null;
            }

            var name = string.IsNullOrWhiteSpace(record.Name)
                ? Path.GetFileNameWithoutExtension(path)
                : record.Name.Trim();

            var groups = record.Groups
                .Where(group => group.Items.Count > 0 && group.Weight > 0)
                .Select(group => new StaticPlacementGroup(
                    group.Weight,
                    group.Items.Select(item => new StaticPlacementItem(
                        item.TileId,
                        item.X,
                        item.Y,
                        item.Z,
                        item.Hue)).ToList()))
                .ToList();

            return new StaticPlacementDefinition(name, record.Chance, groups);
        }
        catch
        {
            return null;
        }
    }

    public static void SaveStaticDefinition(string path, StaticPlacementDefinition definition)
    {
        var record = new StaticPlacementDefinitionRecord(
            definition.Name,
            definition.Chance,
            definition.Groups.Select(group => new StaticPlacementGroupRecord(
                group.Weight,
                group.Items.Select(item => new StaticPlacementItemRecord(
                    item.TileId,
                    item.X,
                    item.Y,
                    item.Z,
                    item.Hue)).ToList()
            )).ToList()
        );

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var json = JsonSerializer.Serialize(record, JsonOptions);
        File.WriteAllText(path, json);
    }
}

public sealed record TerrainDefinitionRecord(
    string? Name,
    ushort TileId,
    int? Id = null,
    byte? R = null,
    byte? G = null,
    byte? B = null,
    int? Base = null,
    bool? Random = null);

public sealed record StaticPlacementDefinitionRecord(
    string? Name,
    int Chance,
    List<StaticPlacementGroupRecord> Groups);

public sealed record StaticPlacementGroupRecord(
    int Weight,
    List<StaticPlacementItemRecord> Items);

public sealed record StaticPlacementItemRecord(
    ushort TileId,
    int X,
    int Y,
    sbyte Z,
    ushort Hue);
