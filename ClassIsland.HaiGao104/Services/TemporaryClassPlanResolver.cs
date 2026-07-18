using ClassIsland.Shared.Models.Profile;

namespace ClassIsland.HaiGao104.Services;

internal static class TemporaryClassPlanResolver
{
    public static bool TryGetValid(Profile profile, DateTime date, out Guid classPlanId)
    {
        if (profile.TempClassPlanId is { } configuredId &&
            configuredId != Guid.Empty &&
            profile.TempClassPlanSetupTime.Date >= date.Date &&
            profile.ClassPlans.ContainsKey(configuredId))
        {
            classPlanId = configuredId;
            return true;
        }

        classPlanId = Guid.Empty;
        return false;
    }
}
