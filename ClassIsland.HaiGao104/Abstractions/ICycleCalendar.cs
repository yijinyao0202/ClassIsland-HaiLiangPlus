namespace ClassIsland.HaiGao104.Abstractions;

public interface ICycleCalendar
{
    bool IsCycleActive { get; }

    bool IsRestDay(DateTime date);

    event EventHandler? SettingsChanged;
}
