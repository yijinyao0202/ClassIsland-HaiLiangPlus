namespace ClassIsland.HaiGaoDuty.Models;

internal sealed class DutyRosterPersistentState
{
    public int SchemaVersion { get; set; } = 1;

    public bool IsEnabled { get; set; }

    public TimeSpan ReminderTime { get; set; } = new(7, 30, 0);

    public bool IsSpeechEnabled { get; set; } = true;

    public DutyRosterConfiguration? ActiveConfiguration { get; set; }

    public DutyRosterConfiguration? PendingConfiguration { get; set; }

    public List<string> CurrentOrder { get; set; } = [];

    public int CurrentOrderCursor { get; set; }

    public int CycleNumber { get; set; }

    public DateTime? LastProcessedDate { get; set; }

    public DateTime? AssignmentDate { get; set; }

    public List<string> TodayAssignees { get; set; } = [];

    public DateTime? LastNotifiedDate { get; set; }
}
