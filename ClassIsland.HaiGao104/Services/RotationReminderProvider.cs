using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Abstractions.Services.NotificationProviders;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Models.Notification;

namespace ClassIsland.HaiGao104.Services;

[NotificationProviderInfo(
    "97A32193-B15C-467A-99C4-CB2160809F24",
    "HL Education + 轮换提醒",
    "\ue112",
    "按用户自定义规则提醒整个班级执行哪一个批次，不修改课表或时间表。")]
public sealed class RotationReminderProvider : NotificationProviderBase
{
    private readonly CycleSettingsService _settings;
    private readonly IExactTimeService _exactTimeService;
    private DateTime? _lastNotificationTime;

    public RotationReminderProvider(
        CycleSettingsService settings,
        ILessonsService lessonsService,
        IExactTimeService exactTimeService)
    {
        _settings = settings;
        _exactTimeService = exactTimeService;
        lessonsService.PreMainTimerTicked += OnPreMainTimerTicked;
    }

    private void OnPreMainTimerTicked(object? sender, EventArgs e)
    {
        var now = _exactTimeService.GetCurrentLocalDateTime();
        if (!_settings.IsEnabled ||
            !_settings.IsRotationEnabled ||
            _settings.IsRestDay(now))
        {
            return;
        }

        var rotationStep = _settings.GetRotationStep(now);
        if (rotationStep is null)
        {
            return;
        }

        var targetTime = now.Date + rotationStep.Time;
        if (!ShouldNotify(now, targetTime, _lastNotificationTime))
        {
            return;
        }

        _lastNotificationTime = targetTime;
        var stepName = string.IsNullOrWhiteSpace(rotationStep.Name) ? "当前批次" : rotationStep.Name.Trim();
        var message = $"{_settings.RotationName}：本班全体执行“{stepName}”，时间 {targetTime:HH:mm}";
        ShowNotification(new NotificationRequest
        {
            MaskContent = NotificationContent.CreateTwoIconsMask(message, factory: content =>
            {
                content.Duration = TimeSpan.FromSeconds(5);
                content.SpeechContent = $"现在是{_settings.RotationName}，本班全体执行{stepName}。";
                content.IsSpeechEnabled = _settings.IsRotationSpeechEnabled;
            })
        });
    }

    internal static bool ShouldNotify(DateTime now, DateTime targetTime, DateTime? lastNotificationTime) =>
        now >= targetTime &&
        now < targetTime.AddSeconds(30) &&
        lastNotificationTime != targetTime;
}
