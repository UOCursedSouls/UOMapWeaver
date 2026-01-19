namespace UOMapWeaver.Core.Statics;

public enum StaticsLayout
{
    RowMajor,
    ColumnMajor
}

public static class StaticsLayoutHelper
{
    public static int GetBlockIndex(int blockX, int blockY, int blockWidth, int blockHeight, StaticsLayout layout)
    {
        return layout == StaticsLayout.ColumnMajor
            ? blockX * blockHeight + blockY
            : blockY * blockWidth + blockX;
    }
}
