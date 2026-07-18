using ClassIsland.HaiGao104.Services;
using Xunit;

namespace ClassIsland.HaiGaoDuty.Tests;

public sealed class CycleSettingsServiceTests
{
    [Fact]
    public void TakeoverRequiresOnboardingAndPersistsTheDecision()
    {
        WithSettings(settingsPath =>
        {
            var settings = new CycleSettingsService(settingsPath);

            settings.IsEnabled = true;

            Assert.False(settings.IsEnabled);
            Assert.False(settings.IsTakeoverEnabled);

            settings.CompleteOnboarding(true);

            Assert.True(settings.IsTakeoverEnabled);
            Assert.True(new CycleSettingsService(settingsPath).IsTakeoverEnabled);
        });
    }

    [Fact]
    public void CalibrationSupportsTwentyFourWorkDaysAndPersists()
    {
        WithSettings(settingsPath =>
        {
            var today = new DateTime(2026, 7, 19);
            var settings = CreateEnabledSettings(settingsPath);
            settings.WorkDays = 24;
            settings.RestDays = 4;

            settings.SetTodayWorkDay(12, today);

            Assert.Equal(11, settings.GetCyclePosition(today));
            Assert.True(settings.TryGetEffectiveWorkDayIndex(today.AddDays(12), out var finalWorkDay));
            Assert.Equal(23, finalWorkDay);
            Assert.True(settings.IsRestDay(today.AddDays(13)));

            var restored = new CycleSettingsService(settingsPath);
            Assert.Equal(24, restored.WorkDays);
            Assert.Equal(11, restored.GetCyclePosition(today));
        });
    }

    [Fact]
    public void RestRepeatWrapsAtTheLastWorkDayAndEndsWithTheRestSegment()
    {
        WithSettings(settingsPath =>
        {
            var today = new DateTime(2026, 7, 19);
            var settings = CreateEnabledSettings(settingsPath);
            settings.WorkDays = 12;
            settings.RestDays = 4;
            settings.AnchorDate = today.AddDays(-12);

            Assert.True(settings.IsNominalRestDay(today));

            settings.StartRestRepeat(today, 12);

            Assert.Equal(today.AddDays(3), settings.RestRepeatEndDate);
            var expectedIndexes = new[] { 11, 0, 1, 2 };
            for (var offset = 0; offset < expectedIndexes.Length; offset++)
            {
                var date = today.AddDays(offset);
                Assert.True(settings.TryGetEffectiveWorkDayIndex(date, out var workDayIndex));
                Assert.Equal(expectedIndexes[offset], workDayIndex);
                Assert.False(settings.IsRestDay(date));
            }

            Assert.True(settings.TryGetEffectiveWorkDayIndex(today.AddDays(4), out var nextCycleIndex));
            Assert.Equal(0, nextCycleIndex);
        });
    }

    [Fact]
    public void TemporaryClassPlanOverridesOnlyItsOwnRestDate()
    {
        WithSettings(settingsPath =>
        {
            var today = new DateTime(2026, 7, 19);
            var settings = CreateEnabledSettings(settingsPath);
            settings.AnchorDate = today.AddDays(-settings.WorkDays);

            Assert.True(settings.IsRestDay(today));

            settings.SetTemporaryClassPlanOverrideDate(today);

            Assert.False(settings.IsRestDay(today));
            Assert.True(settings.IsRestDay(today.AddDays(1)));
        });
    }

    [Fact]
    public void CyclePlanMutationKeepsTheCurrentRestOrdinal()
    {
        WithSettings(settingsPath =>
        {
            var today = new DateTime(2026, 7, 19);
            var settings = CreateEnabledSettings(settingsPath);
            settings.WorkDays = 10;
            settings.RestDays = 4;
            settings.AnchorDate = today.AddDays(-11);
            var planIds = Enumerable.Range(0, 12).Select(_ => Guid.NewGuid()).ToArray();

            settings.ApplyCyclePlanMutation(planIds, [], today, 14, null);

            Assert.Equal(12, settings.WorkDays);
            Assert.Equal(13, settings.GetCyclePosition(today));
            Assert.True(settings.IsNominalRestDay(today));
        });
    }

    private static CycleSettingsService CreateEnabledSettings(string settingsPath)
    {
        var settings = new CycleSettingsService(settingsPath);
        settings.CompleteOnboarding(true);
        return settings;
    }

    private static void WithSettings(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), $"cycle-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            action(Path.Combine(root, "settings.json"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
