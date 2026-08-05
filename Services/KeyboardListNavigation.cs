namespace TechBench.Services;

internal static class KeyboardListNavigation
{
    public static int GetNextIndex(
        int itemCount,
        int currentIndex,
        bool moveDown)
    {
        if (itemCount <= 0)
        {
            return -1;
        }

        if (currentIndex < 0 || currentIndex >= itemCount)
        {
            return moveDown ? 0 : itemCount - 1;
        }

        return moveDown
            ? Math.Min(currentIndex + 1, itemCount - 1)
            : Math.Max(currentIndex - 1, 0);
    }
}
