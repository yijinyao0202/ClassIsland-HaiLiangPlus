using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared.Models.Profile;
using Microsoft.Extensions.Hosting;

namespace ClassIsland.HaiGao104.Services;

public sealed class CycleScheduleService(
    CycleSettingsService settings,
    CyclePlanService cyclePlanService,
    IScheduleControlBridge scheduleBridge,
    IExactTimeService exactTimeService) : IHostedService
{
    private bool _isApplying;
    private bool _isTakingOver;
    private bool _originalClassPlanEnabled;
    private readonly Dictionary<DateTime, OrderedSchedule?> _originalOrderedSchedules = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        scheduleBridge.PreMainTimerTicked += OnPreMainTimerTicked;
        settings.SettingsChanged += OnSettingsChanged;
        ApplyScheduleState();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        scheduleBridge.PreMainTimerTicked -= OnPreMainTimerTicked;
        settings.SettingsChanged -= OnSettingsChanged;
        settings.SetTemporaryClassPlanOverrideDate(null);
        RestoreOriginalState();
        return Task.CompletedTask;
    }

    private void OnPreMainTimerTicked(object? sender, EventArgs e) => ApplyScheduleState();

    private void OnSettingsChanged(object? sender, EventArgs e) => ApplyScheduleState();

    private void ApplyScheduleState()
    {
        if (_isApplying)
        {
            return;
        }

        _isApplying = true;
        try
        {
            var now = exactTimeService.GetCurrentLocalDateTime();
            if (!settings.IsTakeoverEnabled)
            {
                settings.SetTemporaryClassPlanOverrideDate(null);
                settings.RefreshStatus(now);
                RestoreOriginalState();
                return;
            }

            BeginTakeover();
            RestoreOrderedSchedulesExcept(now.Date);
            var managedClassPlanIds = cyclePlanService.EnsureManagedSchedules();
            var hasTemporaryClassPlan = TemporaryClassPlanResolver.TryGetValid(
                scheduleBridge.Profile,
                now,
                out var temporaryClassPlanId);
            settings.SetTemporaryClassPlanOverrideDate(hasTemporaryClassPlan ? now.Date : null);
            settings.RefreshStatus(now);

            Guid? selectedClassPlanId = null;
            if (hasTemporaryClassPlan)
            {
                selectedClassPlanId = temporaryClassPlanId;
            }
            else if (settings.TryGetEffectiveWorkDayIndex(now, out var workDayIndex) &&
                     workDayIndex >= 0 &&
                     workDayIndex < managedClassPlanIds.Count)
            {
                selectedClassPlanId = managedClassPlanIds[workDayIndex];
            }

            if (selectedClassPlanId is null)
            {
                RestoreOrderedSchedule(now.Date);
                scheduleBridge.IsClassPlanEnabled = false;
                scheduleBridge.CurrentClassPlan = null;
                scheduleBridge.IsClassPlanLoaded = false;
                settings.SetDisabledByPlugin(true);
                return;
            }

            OverrideOrderedSchedule(now.Date, selectedClassPlanId.Value);
            scheduleBridge.IsClassPlanEnabled = true;
            scheduleBridge.IsClassPlanLoaded = false;
            settings.SetDisabledByPlugin(false);
        }
        finally
        {
            _isApplying = false;
        }
    }

    private void BeginTakeover()
    {
        if (_isTakingOver)
        {
            return;
        }

        _originalClassPlanEnabled = scheduleBridge.IsClassPlanEnabled;
        _isTakingOver = true;
    }

    private void OverrideOrderedSchedule(DateTime date, Guid classPlanId)
    {
        var schedules = scheduleBridge.Profile.OrderedSchedules;
        if (!_originalOrderedSchedules.ContainsKey(date))
        {
            _originalOrderedSchedules[date] = schedules.GetValueOrDefault(date);
        }

        if (!schedules.TryGetValue(date, out var current) || current.ClassPlanId != classPlanId)
        {
            schedules[date] = new OrderedSchedule { ClassPlanId = classPlanId };
        }
    }

    private void RestoreOrderedSchedulesExcept(DateTime date)
    {
        foreach (var trackedDate in _originalOrderedSchedules.Keys.Where(item => item != date).ToArray())
        {
            RestoreOrderedSchedule(trackedDate);
        }
    }

    private void RestoreOrderedSchedule(DateTime date)
    {
        if (!_originalOrderedSchedules.Remove(date, out var originalSchedule))
        {
            return;
        }

        var schedules = scheduleBridge.Profile.OrderedSchedules;
        if (originalSchedule is null)
        {
            schedules.Remove(date);
        }
        else
        {
            schedules[date] = originalSchedule;
        }
    }

    private void RestoreOriginalState()
    {
        if (!_isTakingOver)
        {
            if (settings.WasDisabledByPlugin)
            {
                scheduleBridge.IsClassPlanEnabled = true;
                settings.SetDisabledByPlugin(false);
            }
            return;
        }

        foreach (var date in _originalOrderedSchedules.Keys.ToArray())
        {
            RestoreOrderedSchedule(date);
        }
        scheduleBridge.IsClassPlanEnabled = _originalClassPlanEnabled;
        scheduleBridge.IsClassPlanLoaded = false;
        settings.SetDisabledByPlugin(false);
        _isTakingOver = false;
    }
}
