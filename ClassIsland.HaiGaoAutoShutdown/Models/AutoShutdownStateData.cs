namespace ClassIsland.HaiGaoAutoShutdown.Models;

public sealed class AutoShutdownStateData
{
    public DateTime? LastHandledOccurrence { get; set; }

    public string LastOutcome { get; set; } = string.Empty;
}
