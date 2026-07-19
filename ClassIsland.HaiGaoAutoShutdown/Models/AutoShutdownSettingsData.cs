using System.Text.Json.Serialization;

namespace ClassIsland.HaiGaoAutoShutdown.Models;

public sealed class AutoShutdownSettingsData
{
    public int SchemaVersion { get; set; }

    public bool IsEnabled { get; set; }

    public List<AutoShutdownScheduleData>? Schedules { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TimeSpan? ShutdownTime { get; set; }

    public int CountdownSeconds { get; set; } = 60;
}
