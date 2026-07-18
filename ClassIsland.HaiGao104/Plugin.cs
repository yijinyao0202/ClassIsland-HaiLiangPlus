using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.HaiGaoAutoShutdown.Services;
using ClassIsland.HaiGaoDuty.Controls.Components;
using ClassIsland.HaiGaoDuty.Services;
using ClassIsland.HaiGaoDuty.Views.SettingsPages;
using ClassIsland.HaiGao104.Abstractions;
using ClassIsland.HaiGao104.Services;
using ClassIsland.HaiGao104.Views.Components;
using ClassIsland.HaiGao104.Views.SettingsPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassIsland.HaiGao104;

[PluginEntrance]
public sealed class Plugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        var settings = new CycleSettingsService(Path.Combine(PluginConfigFolder, "settings.json"));
        var pluginConfigRoot = Path.GetDirectoryName(PluginConfigFolder);
        var legacyAutoShutdownFolder = string.IsNullOrWhiteSpace(pluginConfigRoot)
            ? null
            : Path.Combine(pluginConfigRoot, "cn.haigao.auto-shutdown");
        var legacyDutyRosterFolder = string.IsNullOrWhiteSpace(pluginConfigRoot)
            ? null
            : Path.Combine(pluginConfigRoot, "cn.haigao.duty-roster");
        var autoShutdownSettingsPath = Path.Combine(PluginConfigFolder, "auto-shutdown.settings.json");
        var autoShutdownStatePath = Path.Combine(PluginConfigFolder, "auto-shutdown.state.json");
        var dutyRosterSettingsPath = Path.Combine(PluginConfigFolder, "duty-roster.settings.json");
        services.AddSingleton(settings);
        services.AddSingleton<ICycleCalendar>(settings);
        services.AddSingleton<IScheduleControlBridge, ScheduleControlBridge>();
        services.AddSingleton<CyclePlanService>();
        services.AddSingleton(serviceProvider => new AutoShutdownSettingsService(
            autoShutdownSettingsPath,
            serviceProvider.GetRequiredService<ILogger<AutoShutdownSettingsService>>(),
            legacyAutoShutdownFolder is null ? null : Path.Combine(legacyAutoShutdownFolder, "settings.json")));
        services.AddSingleton(serviceProvider => new AutoShutdownStateStore(
            autoShutdownStatePath,
            serviceProvider.GetRequiredService<ILogger<AutoShutdownStateStore>>(),
            legacyAutoShutdownFolder is null ? null : Path.Combine(legacyAutoShutdownFolder, "state.json")));
        services.AddSingleton<ICountdownTiming, SystemCountdownTiming>();
        services.AddSingleton<ICountdownDisplayFactory, MultiScreenCountdownDisplayFactory>();
        services.AddSingleton<IShutdownExecutor, WindowsShutdownExecutor>();
        services.AddSingleton<CountdownCoordinator>();
        services.AddSingleton<AutoShutdownSchedulerService>();
        services.AddSingleton<IHostedService>(serviceProvider =>
            serviceProvider.GetRequiredService<AutoShutdownSchedulerService>());
        services.AddSingleton(serviceProvider => new DutyRosterService(
            dutyRosterSettingsPath,
            serviceProvider.GetRequiredService<ICycleCalendar>(),
            serviceProvider.GetRequiredService<IExactTimeService>(),
            serviceProvider.GetRequiredService<ILessonsService>(),
            serviceProvider.GetRequiredService<ILogger<DutyRosterService>>(),
            legacyDutyRosterFolder is null ? null : Path.Combine(legacyDutyRosterFolder, "settings.json")));
        services.AddSingleton<IHostedService>(serviceProvider =>
            serviceProvider.GetRequiredService<DutyRosterService>());
        services.AddSingleton<OnboardingService>();
        services.AddHostedService<OnboardingService>(provider => provider.GetRequiredService<OnboardingService>());
        services.AddHostedService<CycleScheduleService>();
        services.AddHostedService<RestDayPromptService>();
        services.AddHostedService<CycleScheduleEditorHostService>();
        services.AddComponent<HaiGaoCycleComponent>();
        services.AddComponent<DutyRosterComponent>();
        services.AddNotificationProvider<DutyRosterReminderProvider>();
        services.AddSettingsPage<HaiGao104SettingsPage>();
        services.AddSettingsPage<DutyRosterSettingsPage>();
    }
}
