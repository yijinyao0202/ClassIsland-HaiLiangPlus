namespace ClassIsland.HaiGaoAutoShutdown.Models;

public sealed class AutoShutdownScheduleData
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    public TimeSpan ShutdownTime { get; set; } = new(22, 0, 0);
}
