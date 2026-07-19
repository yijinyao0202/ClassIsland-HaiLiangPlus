using Avalonia.Controls;
using Avalonia.Threading;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Services;
using FluentAvalonia.UI.Controls;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassIsland.HaiGao104.Services;

public sealed class RestDayPromptService(
    CycleSettingsService settings,
    IScheduleControlBridge scheduleBridge,
    IExactTimeService exactTimeService,
    ILogger<RestDayPromptService> logger) : IHostedService
{
    private DateTime? _promptedDate;
    private CancellationTokenSource? _lifetimeCancellationSource;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        AppBase.Current.AppStarted += Current_OnAppStarted;
        _lifetimeCancellationSource = new CancellationTokenSource();
        _ = WaitForMainWindowAsync(_lifetimeCancellationSource.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        AppBase.Current.AppStarted -= Current_OnAppStarted;
        _lifetimeCancellationSource?.Cancel();
        _lifetimeCancellationSource?.Dispose();
        _lifetimeCancellationSource = null;
        return Task.CompletedTask;
    }

    private void Current_OnAppStarted(object? sender, EventArgs e) => QueuePrompt();

    private async Task WaitForMainWindowAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var isReady = await Dispatcher.UIThread.InvokeAsync(() =>
                    AppBase.Current.DesktopLifetime?.Windows.Any(window =>
                        window.GetType().FullName == "ClassIsland.MainWindow" &&
                        window.PlatformImpl is not null) == true);
                if (isReady)
                {
                    await Task.Delay(1000, cancellationToken);
                    QueuePrompt();
                    return;
                }

                await Task.Delay(250, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "等待主窗口显示海亮教育+休息日询问时发生错误。");
        }
    }

    private void QueuePrompt() =>
        Dispatcher.UIThread.Post(() => _ = ShowPromptSafelyAsync(), DispatcherPriority.Background);

    private async Task ShowPromptSafelyAsync()
    {
        try
        {
            await ShowPromptIfNeededAsync();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "显示海亮教育+休息日询问失败。");
        }
    }

    private async Task ShowPromptIfNeededAsync()
    {
        var now = exactTimeService.GetCurrentLocalDateTime();
        if (!settings.IsTakeoverEnabled ||
            _promptedDate == now.Date ||
            !settings.IsNominalRestDay(now) ||
            settings.HasActiveRestRepeat(now) ||
            settings.IgnoredRestPromptDate?.Date == now.Date ||
            TemporaryClassPlanResolver.TryGetValid(scheduleBridge.Profile, now, out _))
        {
            return;
        }

        _promptedDate = now.Date;
        var startDayInput = new NumericUpDown
        {
            Minimum = 1,
            Maximum = settings.WorkDays,
            Value = Math.Clamp(settings.RestRepeatStartDay, 1, settings.WorkDays),
            Width = 140,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
        };
        var content = new StackPanel
        {
            Spacing = 8,
            MaxWidth = 460,
            Children =
            {
                new TextBlock
                {
                    Text = "今天按正常周期应为休息日。可从任意周次开始重复课表，或忽略并保持今天休息。",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text = $"输入重复开头（1 至 {settings.WorkDays}，例如 12 表示周十二）：",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                startDayInput,
                new TextBlock
                {
                    Text = "重复仅持续到当前连续休息段结束，超过最后一周后回到周一；下一正常周期仍从周一开始。",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Opacity = 0.75
                }
            }
        };
        var result = await PluginDialogHost.ShowAsync(new ContentDialog
        {
            Title = "今天是休息日",
            Content = content,
            PrimaryButtonText = "重复课表",
            CloseButtonText = "忽略，保持休息",
            DefaultButton = ContentDialogButton.None
        });

        logger.LogInformation("海亮教育+休息日询问已关闭，结果：{Result}", result);

        if (result == ContentDialogResult.Primary)
        {
            var startDay = (int)(startDayInput.Value ?? 1);
            settings.StartRestRepeat(now, startDay);
        }
        else
        {
            settings.IgnoreRestDay(now);
        }
    }
}
