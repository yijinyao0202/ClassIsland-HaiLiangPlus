namespace ClassIsland.HaiGaoDuty.Models;

internal static class DutyRosterStateRepair
{
    public static void RepairDeserializedCollections(DutyRosterPersistentState state)
    {
        state.CurrentOrder = NormalizeNames(state.CurrentOrder);
        state.TodayAssignees = NormalizeNames(state.TodayAssignees);
        if (state.ActiveConfiguration is not null)
        {
            state.ActiveConfiguration.Names = NormalizeNames(state.ActiveConfiguration.Names);
        }
        if (state.PendingConfiguration is not null)
        {
            state.PendingConfiguration.Names = NormalizeNames(state.PendingConfiguration.Names);
        }
    }

    private static List<string> NormalizeNames(IEnumerable<string>? names) =>
        names?
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .ToList() ?? [];
}
