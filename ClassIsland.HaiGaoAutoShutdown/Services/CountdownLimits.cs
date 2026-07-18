namespace ClassIsland.HaiGaoAutoShutdown.Services;

public static class CountdownLimits
{
    public const int MinimumSeconds = 1;
    public const int MaximumSeconds = 3600;
    public const int DefaultSeconds = 60;

    public static int Normalize(int seconds) =>
        Math.Clamp(seconds, MinimumSeconds, MaximumSeconds);
}
