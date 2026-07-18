using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared.Models.Profile;

namespace ClassIsland.HaiGao104.Services;

public interface IScheduleControlBridge
{
    event EventHandler? PreMainTimerTicked;

    bool IsClassPlanEnabled { get; set; }

    bool IsClassPlanLoaded { get; set; }

    ClassPlan? CurrentClassPlan { get; set; }

    Profile Profile { get; }

    void SaveProfile();
}

public sealed class ScheduleControlBridge(
    ILessonsService lessonsService,
    IProfileService profileService) : IScheduleControlBridge
{
    public event EventHandler? PreMainTimerTicked
    {
        add => lessonsService.PreMainTimerTicked += value;
        remove => lessonsService.PreMainTimerTicked -= value;
    }

    public bool IsClassPlanEnabled
    {
        get => lessonsService.IsClassPlanEnabled;
        set => lessonsService.IsClassPlanEnabled = value;
    }

    public bool IsClassPlanLoaded
    {
        get => lessonsService.IsClassPlanLoaded;
        set => lessonsService.IsClassPlanLoaded = value;
    }

    public ClassPlan? CurrentClassPlan
    {
        get => lessonsService.CurrentClassPlan;
        set => lessonsService.CurrentClassPlan = value;
    }

    public Profile Profile => profileService.Profile;

    public void SaveProfile() => profileService.SaveProfile();
}
