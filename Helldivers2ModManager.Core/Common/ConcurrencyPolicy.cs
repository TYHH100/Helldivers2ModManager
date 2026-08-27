namespace Helldivers2ModManager.Core.Common;
public static class ConcurrencyPolicy
{
    public const int MinimumIoParallelism = 2;
    public const int MaximumIoParallelism = 4;
    public static int GetIoParallelism(int processorCount = 0)
    {
        return Math.Clamp(processorCount / 2, MinimumIoParallelism, MaximumIoParallelism);
    }
}
