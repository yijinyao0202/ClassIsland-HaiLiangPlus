using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.HaiGao104.Services;
using ClassIsland.Shared;

namespace ClassIsland.HaiGao104.Views;

public sealed partial class RestDayOverrideControl : UserControl, INotifyPropertyChanged
{
    private readonly CycleSettingsService _settings;
    private readonly IScheduleControlBridge _scheduleBridge;
    private readonly IExactTimeService _exactTimeService;
    private int _repeatStartDay = 1;
    private int _maximumDay = 1;
    private string _statusText = "正在检查今天的作息……";
    private string _repeatButtonText = "从周一开始重复";
    private bool _showRestActions;
    private int _lastSignature;

    public RestDayOverrideControl() : this(
        IAppHost.GetService<CycleSettingsService>(),
        IAppHost.GetService<IScheduleControlBridge>(),
        IAppHost.GetService<IExactTimeService>())
    {
    }

    public RestDayOverrideControl(
        CycleSettingsService settings,
        IScheduleControlBridge scheduleBridge,
        IExactTimeService exactTimeService)
    {
        _settings = settings;
        _scheduleBridge = scheduleBridge;
        _exactTimeService = exactTimeService;
        _repeatStartDay = Math.Clamp(settings.RestRepeatStartDay, 1, settings.WorkDays);
        InitializeComponent();
        DataContext = this;
        Refresh(true);
    }

    public new event PropertyChangedEventHandler? PropertyChanged;

    public int RepeatStartDay
    {
        get => _repeatStartDay;
        set
        {
            var normalized = Math.Clamp(value, 1, MaximumDay);
            if (!SetField(ref _repeatStartDay, normalized))
            {
                return;
            }
            OnPropertyChanged(nameof(RepeatStartDayName));
            RepeatButtonText = $"从{RepeatStartDayName}开始重复";
        }
    }

    public int MaximumDay
    {
        get => _maximumDay;
        private set => SetField(ref _maximumDay, value);
    }

    public string RepeatStartDayName => CycleDayNameFormatter.GetName(RepeatStartDay);

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string RepeatButtonText
    {
        get => _repeatButtonText;
        private set => SetField(ref _repeatButtonText, value);
    }

    public bool ShowRestActions
    {
        get => _showRestActions;
        private set => SetField(ref _showRestActions, value);
    }

    public void RefreshIfChanged() => Refresh(false);

    private void StartRepeat_OnClick(object? sender, RoutedEventArgs e)
    {
        var now = _exactTimeService.GetCurrentLocalDateTime();
        _settings.StartRestRepeat(now, RepeatStartDay);
        Refresh(true);
    }

    private void IgnoreRestDay_OnClick(object? sender, RoutedEventArgs e)
    {
        var now = _exactTimeService.GetCurrentLocalDateTime();
        _settings.IgnoreRestDay(now);
        Refresh(true);
    }

    private void Refresh(bool force)
    {
        var now = _exactTimeService.GetCurrentLocalDateTime();
        var hasTemporaryPlan = TemporaryClassPlanResolver.TryGetValid(
            _scheduleBridge.Profile,
            now,
            out _);
        var signature = HashCode.Combine(
            now.Date,
            _settings.WorkDays,
            _settings.GetCyclePosition(now),
            _settings.RestRepeatStartDate,
            _settings.RestRepeatEndDate,
            _settings.RestRepeatStartDay,
            _settings.IgnoredRestPromptDate,
            hasTemporaryPlan);
        if (!force && signature == _lastSignature)
        {
            return;
        }
        _lastSignature = signature;

        MaximumDay = Math.Max(1, _settings.WorkDays);
        if (RepeatStartDay > MaximumDay)
        {
            RepeatStartDay = MaximumDay;
        }
        ShowRestActions = _settings.IsNominalRestDay(now);
        if (!ShowRestActions)
        {
            StatusText = "今天不是原定休息日；到休息日时可在这里选择重复课表或保持休息。";
        }
        else if (hasTemporaryPlan)
        {
            StatusText = "今天原定休息，但正在使用 ClassIsland 当天临时课表；该临时课表优先级最高。";
        }
        else if (_settings.HasActiveRestRepeat(now))
        {
            StatusText = $"今天原定休息，正在临时重复课表，持续至 {_settings.RestRepeatEndDate:yyyy 年 M 月 d 日}。";
            RepeatStartDay = _settings.RestRepeatStartDay;
        }
        else if (_settings.IgnoredRestPromptDate?.Date == now.Date)
        {
            StatusText = "今天已选择保持休息；仍可在这里改为重复课表。";
        }
        else
        {
            StatusText = "今天原定为休息日，请选择重复课表的开头，或保持休息。";
        }

        RepeatButtonText = $"从{RepeatStartDayName}开始重复";
        OnPropertyChanged(nameof(RepeatStartDayName));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
