using ClassIsland.HaiGao104.Services;

namespace ClassIsland.HaiGao104.Models;

public sealed record ArchivedCyclePlan(
    Guid ClassPlanId,
    int OriginalIndex,
    string OriginalName,
    DateTime ArchivedAt)
{
    public string DisplayText =>
        $"{OriginalName}（原位置 {CycleDayNameFormatter.GetName(OriginalIndex + 1)}）";
}
