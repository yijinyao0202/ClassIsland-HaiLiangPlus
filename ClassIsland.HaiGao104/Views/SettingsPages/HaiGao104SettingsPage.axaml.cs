using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Attributes;
using ClassIsland.HaiGaoAutoShutdown.Models;
using ClassIsland.HaiGaoAutoShutdown.Services;
using ClassIsland.HaiGao104.Services;
using ClassIsland.Shared;
using ClassIsland.Shared.Models.Profile;

namespace ClassIsland.HaiGao104.Views.SettingsPages;

[SettingsPageInfo("cn.haigao.schedule104.settings", "HL Education +")]
public partial class HaiGao104SettingsPage : SettingsPageBase
{
    private bool _isUpdatingTimeLayoutSelection;

    private readonly OnboardingService _onboardingService;

    private readonly CyclePlanService _cyclePlanService;

    private readonly CountdownCoordinator _autoShutdownCountdownCoordinator;

    public CycleSettingsService Settings { get; }

    public IProfileService ProfileService { get; }

    public AutoShutdownSettingsService AutoShutdownSettings { get; }

    public HaiGao104SettingsPage() : this(
        IAppHost.GetService<CycleSettingsService>(),
        IAppHost.GetService<IProfileService>(),
        IAppHost.GetService<OnboardingService>(),
        IAppHost.GetService<CyclePlanService>(),
        IAppHost.GetService<AutoShutdownSettingsService>(),
        IAppHost.GetService<CountdownCoordinator>())
    {
    }

    public HaiGao104SettingsPage(
        CycleSettingsService settings,
        IProfileService profileService,
        OnboardingService onboardingService,
        CyclePlanService cyclePlanService,
        AutoShutdownSettingsService autoShutdownSettings,
        CountdownCoordinator autoShutdownCountdownCoordinator)
    {
        Settings = settings;
        ProfileService = profileService;
        _onboardingService = onboardingService;
        _cyclePlanService = cyclePlanService;
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

    private void AddAutoShutdownSchedule_OnClick(object? sender, RoutedEventArgs e) =>
        AutoShutdownSettings.AddSchedule();

    private void RemoveAutoShutdownSchedule_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AutoShutdownSchedule schedule })
        {
            AutoShutdownSettings.RemoveSchedule(schedule);
        }
    }

    private void RemoveRotationStep_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: RotationStep step })
        {
            Settings.RemoveRotationStep(step);
        }
    }

    private void DuplicateRotationTimeLayout_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: RotationStep step } button &&
            _cyclePlanService.DuplicateRotationTimeLayout(step) is { } duplicatedId &&
            button.Parent is Panel panel)
        {
            var comboBox = panel.Children.OfType<ComboBox>().FirstOrDefault();
            SelectTimeLayout(comboBox, duplicatedId);
        }
    }

    private void TimeLayoutComboBox_OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is ComboBox { DataContext: RotationStep step } comboBox)
        {
            SelectTimeLayout(comboBox, step.TimeLayoutId);
        }
    }

    private void TimeLayoutComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingTimeLayoutSelection ||
            sender is not ComboBox
            {
                DataContext: RotationStep step,
                SelectedItem: KeyValuePair<Guid, TimeLayout> selectedTimeLayout
            })
        {
            return;
        }

        step.TimeLayoutId = selectedTimeLayout.Key;
    }

    private void SelectTimeLayout(ComboBox? comboBox, Guid? timeLayoutId)
    {
        if (comboBox is null)
        {
            return;
        }

        _isUpdatingTimeLayoutSelection = true;
        try
        {
            comboBox.SelectedItem = timeLayoutId is { } id &&
                                    ProfileService.Profile.TimeLayouts.TryGetValue(id, out var timeLayout)
                ? new KeyValuePair<Guid, TimeLayout>(id, timeLayout)
                : null;
        }
        finally
        {
            _isUpdatingTimeLayoutSelection = false;
        }
    }
}
