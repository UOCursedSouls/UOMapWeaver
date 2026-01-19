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

            foreach (var file in Directory.EnumerateFiles(root, "Mod*.json", SearchOption.AllDirectories))
            {
                if (seen.Add(file))
                {
                    results.Add(file);
                }
            }

            foreach (var file in Directory.EnumerateFiles(root, "Mod*.xml", SearchOption.AllDirectories))
            {
                if (seen.Add(file))
                {
                    results.Add(file);
                }
            }
        }

        results.Sort(CompareMapTransFiles);
        return results;
    }

    private static int CompareMapTransFiles(string left, string right)
    {
        var leftName = Path.GetFileNameWithoutExtension(left);
        var rightName = Path.GetFileNameWithoutExtension(right);
        var nameCompare = StringComparer.OrdinalIgnoreCase.Compare(leftName, rightName);
        if (nameCompare != 0)
        {
            return nameCompare;
        }

        var leftPriority = GetExtensionPriority(left);
        var rightPriority = GetExtensionPriority(right);
        if (leftPriority != rightPriority)
        {
            return leftPriority.CompareTo(rightPriority);
        }

        return StringComparer.OrdinalIgnoreCase.Compare(left, right);
    }

    private static int GetExtensionPriority(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
        if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }
        return 2;
    }
}

