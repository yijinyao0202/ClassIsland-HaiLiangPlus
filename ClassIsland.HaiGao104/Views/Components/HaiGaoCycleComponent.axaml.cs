using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Attributes;
using ClassIsland.HaiGao104.Services;
using ClassIsland.Shared;

namespace ClassIsland.HaiGao104.Views.Components;

[ComponentInfo(
    "B523A911-8C11-4D55-90F8-02AEAC7D10A4",
    "HL Education +",
    "\ue304",
    "显示今天对应的周次、周期位置或休息状态。")]
public sealed partial class HaiGaoCycleComponent : ComponentBase
{
    private readonly CycleSettingsService _settings;
    private readonly IScheduleControlBridge _scheduleBridge;
    private readonly IExactTimeService _exactTimeService;
    private string _displayText = "正在计算周期…";

    public HaiGaoCycleComponent() : this(
        IAppHost.GetService<CycleSettingsService>(),
        IAppHost.GetService<IScheduleControlBridge>(),
        IAppHost.GetService<IExactTimeService>())
    {
    }

    public HaiGaoCycleComponent(
        CycleSettingsService settings,
        IScheduleControlBridge scheduleBridge,
        IExactTimeService exactTimeService)
    {
        _settings = settings;
        _scheduleBridge = scheduleBridge;
        _exactTimeService = exactTimeService;
        InitializeComponent();
        DisplayTextBlock.Text = _displayText;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        RefreshDisplay();
    }

    public string DisplayText
    {
        get => _displayText;
        private set
        {
            if (_displayText == value)
            {
                return;
            }
            _displayText = value;
            DisplayTextBlock.Text = value;
        }
    }

    private void OnAttachedToVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _scheduleBridge.PreMainTimerTicked += ScheduleBridge_OnPreMainTimerTicked;
        _settings.SettingsChanged += Settings_OnSettingsChanged;
        RefreshDisplay();
    }

    private void OnDetachedFromVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _scheduleBridge.PreMainTimerTicked -= ScheduleBridge_OnPreMainTimerTicked;
        _settings.SettingsChanged -= Settings_OnSettingsChanged;
    }

    private void ScheduleBridge_OnPreMainTimerTicked(object? sender, EventArgs e) => RefreshDisplay();

    private void Settings_OnSettingsChanged(object? sender, EventArgs e) => RefreshDisplay();

    private void RefreshDisplay()
    {
        if (!_settings.IsEnabled)
        {
            DisplayText = "HL Education + 已暂停";
            return;
        }

        var now = _exactTimeService.GetCurrentLocalDateTime();
        if (TemporaryClassPlanResolver.TryGetValid(_scheduleBridge.Profile, now, out var temporaryPlanId))
        {
            var temporaryIndex = _settings.ManagedClassPlanIds.ToList().IndexOf(temporaryPlanId);
            DisplayText = temporaryIndex >= 0
                ? $"{CycleDayNameFormatter.GetName(temporaryIndex + 1)} · 临时课表"
                : "临时课表";
            return;
        }

        if (_settings.TryGetEffectiveWorkDayIndex(now, out var workDayIndex))
        {
            var cycleText = _settings.HasActiveRestRepeat(now)
                ? $"{CycleDayNameFormatter.GetName(workDayIndex + 1)} · 临时重复"
                : $"{CycleDayNameFormatter.GetName(workDayIndex + 1)} · {workDayIndex + 1}/{_settings.WorkDays}";
            var batchName = _settings.IsRotationEnabled ? _settings.GetRotationStep(now)?.Name?.Trim() : null;
            DisplayText = string.IsNullOrWhiteSpace(batchName) ? cycleText : $"{cycleText} · {batchName}";
            return;
        }

        var restDay = _settings.GetCyclePosition(now) - _settings.WorkDays + 1;
        DisplayText = $"休息日 · {restDay}/{_settings.RestDays}";
    }
}
