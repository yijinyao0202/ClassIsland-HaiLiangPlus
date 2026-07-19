using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Services;
using FluentAvalonia.UI.Controls;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassIsland.HaiGao104.Services;

public sealed class OnboardingService(
    CycleSettingsService settings,
    IUriNavigationService uriNavigationService,
    ILogger<OnboardingService> logger) : IHostedService
{
    private bool _isShowing;
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

    public async Task ShowAsync(bool force = false)
    {
        if (_isShowing || (!force && settings.HasCompletedOnboarding))
        {
            return;
        }

        _isShowing = true;
        var isFirstRun = !settings.HasCompletedOnboarding;
        try
        {
            var content = new StackPanel
            {
                Spacing = 14,
                MaxWidth = 600,
                Children =
                {
                    new TextBlock
                    {
                        Text = "使用前请先了解这五件事",
                        FontSize = 18,
                        FontWeight = FontWeight.SemiBold
                    },
                    new InfoBar
                    {
                        IsOpen = true,
                        IsClosable = false,
                        Severity = InfoBarSeverity.Informational,
                        Title = "非官方个人项目",
                        Message = "海亮教育+并非由海亮教育集团或其关联学校开发、发布或维护。本插件由个人开发者“伊烬遥”独立开发，与海亮教育官方无隶属或授权关系。"
                    },
                    new InfoBar
                    {
                        IsOpen = true,
                        IsClosable = false,
                        Severity = InfoBarSeverity.Warning,
                        Title = "重要：会覆盖 ClassIsland 原软件课表",
                        Message = "启用后，海亮教育+将完全接管每天加载哪份课表，并以多周周期结果覆盖 ClassIsland 原本的每日课表选择。原有课表内容不会被删除；暂停海亮教育+后会恢复接管前的课表状态。"
                    },
                    CreateStep("1", "管理周次和课程", "先在原课表编辑器中添加周一至周 N，并填写全班每天的课程。"),
                    CreateStep("2", "校准今天对应第几周", "设置今天对应的周次后，插件会按上课日与休息日自动向后推算。"),
                    CreateStep("3", "选择班级批次时间表", "第一批、第二批共用课程，只加载各自绑定的时间表；当天临时课表仍具有最高优先级。"),
                    CreateStep("4", "按需开启定时关机", "定时关机默认关闭；请先设置时间和倒计时，并用安全预览确认多屏提示正常。"),
                    CreateStep("5", "配置大周值日生", "值日生默认暂停；在独立的“大周值日生”设置页首次应用名单后会自动开启，并只在海亮教育+课表运行时推进。"),
                    new TextBlock
                    {
                        Text = isFirstRun
                            ? "只有点击“我已了解，开始配置”后，海亮教育+才会开始接管课表。"
                            : "关闭本窗口不会更改当前启用状态。",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.72
                    }
                }
            };

            var result = await PluginDialogHost.ShowAsync(new ContentDialog
            {
                Title = "欢迎使用海亮教育+",
                Content = content,
                PrimaryButtonText = isFirstRun ? "我已了解，开始配置" : "打开设置",
                CloseButtonText = isFirstRun ? "暂不启用" : "关闭",
                DefaultButton = ContentDialogButton.None
            });

            logger.LogInformation("海亮教育+新手引导已关闭，结果：{Result}", result);

            if (isFirstRun)
            {
                settings.CompleteOnboarding(result == ContentDialogResult.Primary);
            }

            if (result == ContentDialogResult.Primary)
            {
                uriNavigationService.Navigate(new Uri("classisland://app/settings/cn.haigao.schedule104.settings"));
            }
        }
        finally
        {
            _isShowing = false;
        }
    }

    private void Current_OnAppStarted(object? sender, EventArgs e) => QueueShow();

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
                    if (!settings.HasCompletedOnboarding)
                    {
                        QueueShow();
                    }
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
            logger.LogError(exception, "等待主窗口显示海亮教育+新手引导时发生错误。");
        }
    }

    private void QueueShow() =>
        Dispatcher.UIThread.Post(() => _ = ShowSafelyAsync(), DispatcherPriority.Background);

    private async Task ShowSafelyAsync()
    {
        try
        {
            await ShowAsync();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "显示海亮教育+新手引导失败。");
        }
    }

    private static StackPanel CreateStep(string number, string title, string description) => new()
    {
        Spacing = 3,
        Children =
        {
            new TextBlock
            {
                Text = $"{number}. {title}",
                FontWeight = FontWeight.SemiBold
            },
            new TextBlock
            {
                Text = description,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.76
            }
        }
    };
}
