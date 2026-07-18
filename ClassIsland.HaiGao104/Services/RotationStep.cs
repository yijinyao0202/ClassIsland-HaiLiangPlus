using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClassIsland.HaiGao104.Services;

public sealed class RotationStep : INotifyPropertyChanged
{
    private string _name;
    private TimeSpan _time;
    private Guid? _timeLayoutId;

    public RotationStep(string name, TimeSpan time, Guid? timeLayoutId = null)
    {
        _name = name;
        _time = time;
        _timeLayoutId = timeLayoutId;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value ?? "");
    }

    public TimeSpan Time
    {
        get => _time;
        set
        {
            var normalized = new TimeSpan(
                PositiveModulo((int)value.TotalHours, 24),
                PositiveModulo(value.Minutes, 60),
                0);
            if (_time == normalized)
            {
                return;
            }

            _time = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Hour));
            OnPropertyChanged(nameof(Minute));
        }
    }

    public int Hour
    {
        get => PositiveModulo((int)_time.TotalHours, 24);
        set => Time = new TimeSpan(Math.Clamp(value, 0, 23), Minute, 0);
    }

    public int Minute
    {
        get => PositiveModulo(_time.Minutes, 60);
        set => Time = new TimeSpan(Hour, Math.Clamp(value, 0, 59), 0);
    }

    public Guid? TimeLayoutId
    {
        get => _timeLayoutId;
        set => SetField(ref _timeLayoutId, value);
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static int PositiveModulo(int value, int divisor) => ((value % divisor) + divisor) % divisor;
}
