namespace TechBench.Controls;

internal static class EquipmentLaneDragPlacement
{
    internal const double LanePitch = 336;

    internal static int ResolveTargetIndex(
        int sourceIndex,
        double horizontalDelta,
        int laneCount)
    {
        if (sourceIndex < 1
            || sourceIndex >= laneCount
            || laneCount <= 1)
        {
            return sourceIndex;
        }

        var columnOffset = (int)Math.Round(
            horizontalDelta / LanePitch,
            MidpointRounding.AwayFromZero);
        return Math.Clamp(
            sourceIndex + columnOffset,
            1,
            laneCount - 1);
    }

    internal static int ResolveTargetIndexFromBoardPosition(
        double boardPositionX,
        int laneCount)
    {
        if (laneCount <= 1)
        {
            return 0;
        }

        var targetIndex = (int)Math.Floor(
            Math.Max(0, boardPositionX) / LanePitch);
        return Math.Clamp(
            targetIndex,
            1,
            laneCount - 1);
    }
}
