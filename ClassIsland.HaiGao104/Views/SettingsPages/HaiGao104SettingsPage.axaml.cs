using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Attributes;
using ClassIsland.HaiGaoAutoShutdown.Services;
using ClassIsland.HaiGao104.Services;
using ClassIsland.Shared;

namespace ClassIsland.HaiGao104.Views.SettingsPages;

[SettingsPageInfo("cn.haigao.schedule104.settings", "海亮教育+")]
public partial class HaiGao104SettingsPage : SettingsPageBase
{
    private readonly OnboardingService _onboardingService;

    private readonly CountdownCoordinator _autoShutdownCountdownCoordinator;

    public CycleSettingsService Settings { get; }

    public IProfileService ProfileService { get; }

    public AutoShutdownSettingsService AutoShutdownSettings { get; }

    public HaiGao104SettingsPage() : this(
        IAppHost.GetService<CycleSettingsService>(),
        IAppHost.GetService<IProfileService>(),
        IAppHost.GetService<OnboardingService>(),
        IAppHost.GetService<AutoShutdownSettingsService>(),
        IAppHost.GetService<CountdownCoordinator>())
    {
    }

    public HaiGao104SettingsPage(
        CycleSettingsService settings,
        IProfileService profileService,
        OnboardingService onboardingService,
        AutoShutdownSettingsService autoShutdownSettings,
        CountdownCoordinator autoShutdownCountdownCoordinator)
    {
        Settings = settings;
        ProfileService = profileService;
        _onboardingService = onboardingService;
        AutoShutdownSettings = autoShutdownSettings;
        _autoShutdownCountdownCoordinator = autoShutdownCountdownCoordinator;
        InitializeComponent();
        DataContext = this;
    }

    private void AddRotationStep_OnClick(object? sender, RoutedEventArgs e) => Settings.AddRotationStep();

    private async void TakeoverToggle_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!Settings.HasCompletedOnboarding)
        {
            await _onboardingService.ShowAsync();
        }
    }

    private async void OpenProfileEditor_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!Settings.HasCompletedOnboarding)
        {
            await _onboardingService.ShowAsync();
        }

        if (Settings.HasCompletedOnboarding)
        {
            IAppHost.GetService<IUriNavigationService>().Navigate(new Uri("classisland://app/profile"));
        }
    }

    private void ShowOnboarding_OnClick(object? sender, RoutedEventArgs e) =>
        _ = _onboardingService.ShowAsync(true);

    private async void AutoShutdownPreview_OnClick(object? sender, RoutedEventArgs e)
    {
        AutoShutdownPreviewButton.IsEnabled = false;
        AutoShutdownSettings.UpdateRuntimeStatus("正在运行安全预览；预览结束后不会执行关机命令。");
        try
        {
            var outcome = await _autoShutdownCountdownCoordinator.RunPreviewAsync(
                AutoShutdownSettings.CountdownSeconds,
                CancellationToken.None);
            AutoShutdownSettings.UpdateRuntimeStatus(outcome switch
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
            AutoShutdownPreviewButton.IsEnabled = true;
        }
    }

    private void RemoveRotationStep_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: RotationStep step })
        {
            Settings.RemoveRotationStep(step);
        }
    }
}
