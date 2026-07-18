using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Abstractions.Services.NotificationProviders;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Models.Notification;
using Microsoft.Extensions.Logging;

namespace ClassIsland.HaiGaoDuty.Services;

[NotificationProviderInfo(
    "E48E2F23-AC08-4D16-B0E3-3C7A14318E65",
    "海亮教育+值日生提醒",
    "\ue0ba",
    "在设定时间提醒当天值日生，每个上课日最多提醒一次。")]
public sealed class DutyRosterReminderProvider : NotificationProviderBase
{
    private readonly DutyRosterService _service;
    private readonly IExactTimeService _exactTimeService;
    private readonly ILogger<DutyRosterReminderProvider> _logger;

    public DutyRosterReminderProvider(
        DutyRosterService service,
        ILessonsService lessonsService,
        IExactTimeService exactTimeService,
        ILogger<DutyRosterReminderProvider> logger)
    {
        _service = service;
        _exactTimeService = exactTimeService;
        _logger = logger;
        lessonsService.PreMainTimerTicked += OnPreMainTimerTicked;
    }

    private void OnPreMainTimerTicked(object? sender, EventArgs e)
    {
        var now = _exactTimeService.GetCurrentLocalDateTime();
        if (!_service.TryMarkReminderDue(now, out var assignees))
        {
            return;
        }

        var names = string.Join("、", assignees);
        try
        {
            ShowNotification(new NotificationRequest
            {
                MaskContent = NotificationContent.CreateTwoIconsMask($"今日值日生：{names}", factory: content =>
                {
                    content.Duration = TimeSpan.FromSeconds(6);
                    content.SpeechContent = $"今天的值日生是，{string.Join("，", assignees)}。";
                    content.IsSpeechEnabled = _service.IsSpeechEnabled;
                })
            });
        }
        catch (Exception exception)
        {
            _service.ReleaseReminderReservation(now.Date);
            _logger.LogError(exception, "显示海亮教育+值日生提醒失败，将在下一次计时器刷新时重试。");
        }
    }
}
