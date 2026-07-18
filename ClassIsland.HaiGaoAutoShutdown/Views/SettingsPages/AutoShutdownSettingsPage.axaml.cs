using Avalonia.Interactivity;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.HaiGaoAutoShutdown.Services;
using ClassIsland.Shared;

namespace ClassIsland.HaiGaoAutoShutdown.Views.SettingsPages;

[SettingsPageInfo("cn.haigao.auto-shutdown.settings", "海高定时关机")]
public partial class AutoShutdownSettingsPage : SettingsPageBase
{
    public AutoShutdownSettingsService Settings { get; }

    private CountdownCoordinator CountdownCoordinator { get; }

    public AutoShutdownSettingsPage()
    {
        Settings = IAppHost.GetService<AutoShutdownSettingsService>();
        CountdownCoordinator = IAppHost.GetService<CountdownCoordinator>();
        InitializeComponent();
        DataContext = this;
    }

    private async void Preview_OnClick(object? sender, RoutedEventArgs args)
    {
        PreviewButton.IsEnabled = false;
        Settings.UpdateRuntimeStatus("正在运行安全预览；预览结束后不会执行关机命令。");
        try
        {
            var outcome = await CountdownCoordinator.RunPreviewAsync(
                Settings.CountdownSeconds,
                CancellationToken.None);
            Settings.UpdateRuntimeStatus(outcome switch
            {
                CountdownOutcome.Cancelled => "安全预览已由用户取消，未执行关机命令。",
                CountdownOutcome.PreviewCompleted => "安全预览完成，未执行关机命令。",
                CountdownOutcome.PresentationFailed => "无法覆盖全部屏幕，安全预览已取消。",
                CountdownOutcome.AlreadyRunning => "已有一个倒计时正在运行。",
                _ => "安全预览已结束，未执行关机命令。"
            });
        }
        finally
        {
            PreviewButton.IsEnabled = true;
        }
    }
}
