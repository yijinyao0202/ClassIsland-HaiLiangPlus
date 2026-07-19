using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClassIsland.HaiGaoAutoShutdown.Models;

public sealed class AutoShutdownSchedule : INotifyPropertyChanged
{
    private string _name;
    private bool _isEnabled;
    private TimeSpan _shutdownTime;

    internal AutoShutdownSchedule(Guid id, string name, bool isEnabled, TimeSpan shutdownTime)
    {
        Id = id;
        _name = name;
        _isEnabled = isEnabled;
        _shutdownTime = NormalizeTime(shutdownTime);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal event EventHandler? Changed;

    public Guid Id { get; }

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value ?? string.Empty);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetField(ref _isEnabled, value);
    }

    public TimeSpan ShutdownTime
    {
        get => _shutdownTime;
        set => SetField(ref _shutdownTime, NormalizeTime(value));
    }

    internal AutoShutdownScheduleData ToData(int index) => new()
    {
        Id = Id,
        Name = string.IsNullOrWhiteSpace(Name) ? $"关机计划 {index + 1}" : Name.Trim(),
        IsEnabled = IsEnabled,
        ShutdownTime = ShutdownTime
    };

    private static TimeSpan NormalizeTime(TimeSpan value) =>
        value >= TimeSpan.Zero && value < TimeSpan.FromDays(1)
            ? new TimeSpan(value.Hours, value.Minutes, 0)
            : new TimeSpan(22, 0, 0);

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed record AutoShutdownScheduleSnapshot(
    Guid Id,
    string Name,
    TimeSpan ShutdownTime);

public sealed record AutoShutdownOccurrence(
    Guid ScheduleId,
    string ScheduleName,
    DateTime OccursAt);
