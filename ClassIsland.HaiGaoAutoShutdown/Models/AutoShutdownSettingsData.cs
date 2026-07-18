namespace ClassIsland.HaiGaoAutoShutdown.Models;

public sealed class AutoShutdownSettingsData
{
    public bool IsEnabled { get; set; }

    public TimeSpan ShutdownTime { get; set; } = new(22, 0, 0);

    public int CountdownSeconds { get; set; } = 60;
}
