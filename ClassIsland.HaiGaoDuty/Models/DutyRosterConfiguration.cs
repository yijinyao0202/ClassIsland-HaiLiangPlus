namespace ClassIsland.HaiGaoDuty.Models;

internal sealed class DutyRosterConfiguration
{
    public List<string> Names { get; set; } = [];

    public int DailyCount { get; set; } = 1;

    public DutyRosterConfiguration Clone() => new()
    {
        Names = [.. Names],
        DailyCount = DailyCount
    };
}
