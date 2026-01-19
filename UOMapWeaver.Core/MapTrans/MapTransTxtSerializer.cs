using System.Globalization;
using System.Text;

namespace UOMapWeaver.Core.MapTrans;

public static class MapTransTxtSerializer
{
    public static void Save(string path, MapTransProfile profile)
    {
        var builder = new StringBuilder();
        foreach (var entry in profile.Entries)
        {
            if (entry.TileIds.Count == 0)
            {
                continue;
            }

            if (entry.Group.HasValue)
            {
                builder.AppendFormat(CultureInfo.InvariantCulture, "{0:X2} {1:X2} {2}",
                    entry.ColorIndex, entry.Group.Value, entry.Altitude);
            }
            else
            {
                builder.AppendFormat(CultureInfo.InvariantCulture, "{0:X2} {1}",
                    entry.ColorIndex, entry.Altitude);
            }

            foreach (var tileId in entry.TileIds)
            {
                builder.Append(' ');
                builder.Append(tileId.ToString("X4", CultureInfo.InvariantCulture));
            }

            builder.AppendLine();
        }

        File.WriteAllText(path, builder.ToString());
    }
}
