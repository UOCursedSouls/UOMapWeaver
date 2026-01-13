namespace UOMapWeaver.Core.MapTrans;

public static class MapTransCatalog
{
    public static IReadOnlyList<string> FindMapTransFiles(IEnumerable<string> roots)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(root, "Mod*.txt", SearchOption.AllDirectories))
            {
                if (seen.Add(file))
                {
                    results.Add(file);
                }
            }
        }

        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }
}

