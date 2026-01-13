namespace UOMapWeaver.Core.MapTrans;

public sealed class MapTransProfile
{
    public MapTransProfile(string name, IReadOnlyList<MapTransEntry> entries, string? palettePath)
    {
        Name = name;
        Entries = entries;
        PalettePath = palettePath;
        EntriesByColor = BuildLookup(entries);
    }

    public string Name { get; }

    public IReadOnlyList<MapTransEntry> Entries { get; }

    public string? PalettePath { get; }

    public IReadOnlyDictionary<byte, IReadOnlyList<MapTransEntry>> EntriesByColor { get; }

    private static IReadOnlyDictionary<byte, IReadOnlyList<MapTransEntry>> BuildLookup(IReadOnlyList<MapTransEntry> entries)
    {
        var lookup = new Dictionary<byte, List<MapTransEntry>>();

        foreach (var entry in entries)
        {
            if (!lookup.TryGetValue(entry.ColorIndex, out var list))
            {
                list = new List<MapTransEntry>();
                lookup[entry.ColorIndex] = list;
            }

            list.Add(entry);
        }

        var result = new Dictionary<byte, IReadOnlyList<MapTransEntry>>();
        foreach (var pair in lookup)
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }
}

