using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.HaiGaoAutoShutdown.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassIsland.HaiGaoAutoShutdown.Services;

public sealed class AutoShutdownSchedulerService(
    AutoShutdownSettingsService settings,
    AutoShutdownStateStore stateStore,
    CountdownCoordinator countdownCoordinator,
    IExactTimeService exactTimeService,
    ILogger<AutoShutdownSchedulerService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaximumContinuousGap = TimeSpan.FromSeconds(2);
    private int _settingsRevision;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        settings.SettingsChanged += OnSettingsChanged;
        var observedRevision = Volatile.Read(ref _settingsRevision);
        var previousSample = exactTimeService.GetCurrentLocalDateTime();
        AutoShutdownOccurrence? nextOccurrence = settings.IsEnabled ? GetNextOccurrence(previousSample) : null;
        PublishNext(nextOccurrence);

        try
        {
            using var timer = new PeriodicTimer(PollInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var now = exactTimeService.GetCurrentLocalDateTime();
                var currentRevision = Volatile.Read(ref _settingsRevision);
                if (currentRevision != observedRevision)
                {
                    observedRevision = currentRevision;
                    if (!settings.IsEnabled)
                    {
                        countdownCoordinator.CancelScheduled();
                        nextOccurrence = null;
                        PublishStatus("插件已暂停，当前计划不会执行。");
                    }
                    else
                    {
                        nextOccurrence = GetNextOccurrence(now);
                        PublishStatus(nextOccurrence is null
                            ? "当前没有启用的关机计划，新增或开启计划后才会执行。"
                            : "设置已保存，关机计划已更新。");
                    }
                    previousSample = now;
                    PublishNext(nextOccurrence);
                    continue;
                }

                if (!settings.IsEnabled)
                {
                    nextOccurrence = null;
                    previousSample = now;
                    continue;
                }
                nextOccurrence ??= GetNextOccurrence(now);
                if (nextOccurrence is null)
                {
                    previousSample = now;
                    continue;
                }

                var crossing = DailyScheduleCalculator.EvaluateCrossing(
                    previousSample,
                    now,
                    nextOccurrence.OccursAt,
                    MaximumContinuousGap);
                previousSample = now;
                switch (crossing)
                {
                    case ScheduleCrossingResult.None:
                        continue;
                    case ScheduleCrossingResult.ClockMovedBackward:
                        nextOccurrence = GetNextOccurrence(now);
                        PublishNext(nextOccurrence);
                        PublishStatus("检测到系统时间回拨，已重新计算计划并避免重复触发。");
                        continue;
                    case ScheduleCrossingResult.Skip:
                    {
                        var skippedOccurrence = DailyScheduleCalculator.GetLatestOccurrenceAtOrBefore(
                                                    now,
                                                    settings.GetEnabledScheduleSnapshot())
                                                ?? nextOccurrence;
                        stateStore.MarkHandled(skippedOccurrence.OccursAt, "Skipped");
                        nextOccurrence = GetNextOccurrence(now);
                        PublishNext(nextOccurrence);
                        PublishStatus($"已跳过休眠或离线期间错过的关机计划，截至“{skippedOccurrence.ScheduleName}”（{skippedOccurrence.OccursAt:MM-dd HH:mm}）。");
                        continue;
                    }
                    case ScheduleCrossingResult.Trigger:
                    {
                        var occurrence = nextOccurrence;
                        stateStore.MarkHandled(occurrence.OccursAt, "Triggered");
                        nextOccurrence = GetNextOccurrence(occurrence.OccursAt);
                        PublishNext(nextOccurrence);
                        PublishStatus($"“{occurrence.ScheduleName}”关机倒计时正在显示，点击任意屏幕可取消本次关机。");
                        var outcome = await countdownCoordinator.RunScheduledAsync(
                            settings.CountdownSeconds,
                            stoppingToken);
                        stateStore.MarkHandled(occurrence.OccursAt, outcome.ToString());
                        PublishStatus(FormatOutcome(outcome));
                        previousSample = exactTimeService.GetCurrentLocalDateTime();
                        continue;
                    }
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "定时关机调度服务异常停止。");
            PublishStatus("调度服务发生错误，插件已停止执行关机计划。");
        }
        finally
        {
            settings.SettingsChanged -= OnSettingsChanged;
            countdownCoordinator.CancelActive();
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs args)
    {
        Interlocked.Increment(ref _settingsRevision);
        if (!settings.IsEnabled)
        {
            countdownCoordinator.CancelScheduled();
        }
    }

    private AutoShutdownOccurrence? GetNextOccurrence(DateTime now) =>
        DailyScheduleCalculator.GetNextOccurrence(
            now,
            settings.GetEnabledScheduleSnapshot(),
            stateStore.LastHandledOccurrence);

    private void PublishNext(AutoShutdownOccurrence? occurrence) =>
        PublishOnUiThread(() => settings.UpdateNextOccurrence(occurrence));

    private void PublishStatus(string status) =>
        PublishOnUiThread(() => settings.UpdateRuntimeStatus(status));

    private static void PublishOnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
        }
    }

    private static string FormatOutcome(CountdownOutcome outcome) => outcome switch
    {
        CountdownOutcome.Cancelled => "本次关机已取消，后续计划仍会按设置执行。",
        CountdownOutcome.ShutdownStarted => "已向 Windows 提交正常关机请求。",
        CountdownOutcome.PresentationFailed => "倒计时窗口未能覆盖全部屏幕，本次关机已安全取消。",
        CountdownOutcome.ShutdownFailed => "Windows 关机命令执行失败，本次不会自动重试。",
        CountdownOutcome.AlreadyRunning => "已有倒计时正在运行，本次计划未重复显示。",
        CountdownOutcome.PreviewCompleted => "安全预览已完成，未执行关机命令。",
        _ => "本次计划已结束。"
    };
}
