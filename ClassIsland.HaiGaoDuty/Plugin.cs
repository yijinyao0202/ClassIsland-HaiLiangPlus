using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.HaiGao104.Abstractions;
using ClassIsland.HaiGaoDuty.Controls.Components;
using ClassIsland.HaiGaoDuty.Services;
using ClassIsland.HaiGaoDuty.Views.SettingsPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassIsland.HaiGaoDuty;

[PluginEntrance]
public sealed class Plugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        var settingsPath = Path.Combine(PluginConfigFolder, "settings.json");
        services.AddSingleton(sp => new DutyRosterService(
            settingsPath,
            sp.GetService<ICycleCalendar>(),
            sp.GetRequiredService<IExactTimeService>(),
            sp.GetRequiredService<ILessonsService>(),
            sp.GetRequiredService<ILogger<DutyRosterService>>()));
        services.AddHostedService(sp => sp.GetRequiredService<DutyRosterService>());
        services.AddComponent<DutyRosterComponent>();
        services.AddNotificationProvider<DutyRosterReminderProvider>();
        services.AddSettingsPage<DutyRosterSettingsPage>();
    }
}
