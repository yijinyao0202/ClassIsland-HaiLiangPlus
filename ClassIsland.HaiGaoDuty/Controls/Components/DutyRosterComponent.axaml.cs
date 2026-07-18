using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.HaiGaoDuty.Services;
using ClassIsland.Shared;

namespace ClassIsland.HaiGaoDuty.Controls.Components;

[ComponentInfo(
    "5F759BCA-C064-485D-B7A1-AE6656C13D30",
    "今日值日生",
    "\ue73a",
    "显示今天的值日生安排及上课日状态。")]
public partial class DutyRosterComponent : ComponentBase
{
    public DutyRosterService DutyRoster { get; }

    public DutyRosterComponent()
    {
        DutyRoster = IAppHost.GetService<DutyRosterService>();
        InitializeComponent();
        DataContext = DutyRoster;
    }
}
