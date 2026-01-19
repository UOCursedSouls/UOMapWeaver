namespace UOMapWeaver.Core.TileColors;

public readonly struct TileColorProgress
{
    public TileColorProgress(double percent, long processedTiles, long totalTiles)
    {
        Percent = percent;
        ProcessedTiles = processedTiles;
        TotalTiles = totalTiles;
    }

    public double Percent { get; }

    public long ProcessedTiles { get; }

    public long TotalTiles { get; }
}
