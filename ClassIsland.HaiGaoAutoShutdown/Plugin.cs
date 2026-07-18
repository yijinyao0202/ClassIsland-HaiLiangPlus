using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.HaiGaoAutoShutdown.Services;
using ClassIsland.HaiGaoAutoShutdown.Views.SettingsPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassIsland.HaiGaoAutoShutdown;

[PluginEntrance]
public sealed class Plugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        var settingsPath = Path.Combine(PluginConfigFolder, "settings.json");
        var statePath = Path.Combine(PluginConfigFolder, "state.json");

        services.AddSingleton(serviceProvider => new AutoShutdownSettingsService(
            settingsPath,
            serviceProvider.GetRequiredService<ILogger<AutoShutdownSettingsService>>()));
        services.AddSingleton(serviceProvider => new AutoShutdownStateStore(
            statePath,
            serviceProvider.GetRequiredService<ILogger<AutoShutdownStateStore>>()));
        services.AddSingleton<ICountdownTiming, SystemCountdownTiming>();
        services.AddSingleton<ICountdownDisplayFactory, MultiScreenCountdownDisplayFactory>();
        services.AddSingleton<IShutdownExecutor, WindowsShutdownExecutor>();
        services.AddSingleton<CountdownCoordinator>();
        services.AddSingleton<AutoShutdownSchedulerService>();
        services.AddSingleton<IHostedService>(serviceProvider =>
            serviceProvider.GetRequiredService<AutoShutdownSchedulerService>());
        services.AddSettingsPage<AutoShutdownSettingsPage>();
    }
}
