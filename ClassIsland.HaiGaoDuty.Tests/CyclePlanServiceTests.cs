using System.ComponentModel;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.HaiGao104.Services;
using ClassIsland.Shared.Models.Profile;
using Xunit;

namespace ClassIsland.HaiGaoDuty.Tests;

public sealed class CyclePlanServiceTests
{
    [Fact]
    public void AddingRotationStepDoesNotCreateAnotherTimeLayout()
    {
        WithService((settings, profile, _, service, _) =>
        {
            service.EnsureManagedSchedules();
            var countAfterInitialization = profile.TimeLayouts.Count;

            settings.AddRotationStep();
            service.EnsureManagedSchedules();

            Assert.Equal(countAfterInitialization, profile.TimeLayouts.Count);
            Assert.All(settings.RotationSteps, step =>
                Assert.Equal(settings.ManagedTimeLayoutId, step.TimeLayoutId));
        });
    }

    [Fact]
    public void ExistingCustomTimeLayoutSelectionIsPreserved()
    {
        WithService((settings, profile, _, service, customTimeLayoutId) =>
        {
            service.EnsureManagedSchedules();
            var countAfterInitialization = profile.TimeLayouts.Count;
            settings.RotationSteps[1].TimeLayoutId = customTimeLayoutId;

            service.EnsureManagedSchedules();

            Assert.Equal(countAfterInitialization, profile.TimeLayouts.Count);
            Assert.Equal(customTimeLayoutId, settings.RotationSteps[1].TimeLayoutId);
            Assert.Equal("用户自定义时间表", profile.TimeLayouts[customTimeLayoutId].Name);
        });
    }

    [Fact]
    public void TimeLayoutIsDuplicatedOnlyAfterExplicitRequest()
    {
        WithService((settings, profile, bridge, service, _) =>
        {
            service.EnsureManagedSchedules();
            var countBeforeCopy = profile.TimeLayouts.Count;
            var targetStep = settings.RotationSteps[1];

            var duplicatedId = service.DuplicateRotationTimeLayout(targetStep);

            Assert.NotNull(duplicatedId);
            Assert.Equal(countBeforeCopy + 1, profile.TimeLayouts.Count);
            Assert.Equal(duplicatedId, targetStep.TimeLayoutId);
            Assert.StartsWith("第二批时间表", profile.TimeLayouts[duplicatedId!.Value].Name);
            Assert.True(bridge.SaveCount > 0);

            service.EnsureManagedSchedules();
            Assert.Equal(countBeforeCopy + 1, profile.TimeLayouts.Count);
        });
    }

    [Fact]
    public void ExistingTimeLayoutCanBecomeManagedWithoutCopyOrRename()
    {
        WithService((settings, profile, bridge, service, customTimeLayoutId) =>
        {
            service.EnsureManagedSchedules();
            var countBeforeSelection = profile.TimeLayouts.Count;

            var selected = service.SelectManagedTimeLayout(customTimeLayoutId);

            Assert.True(selected);
            Assert.Equal(customTimeLayoutId, settings.ManagedTimeLayoutId);
            Assert.Equal(countBeforeSelection, profile.TimeLayouts.Count);
            Assert.Equal("用户自定义时间表", profile.TimeLayouts[customTimeLayoutId].Name);
            Assert.All(settings.ManagedClassPlanIds, classPlanId =>
                Assert.Equal(customTimeLayoutId, profile.ClassPlans[classPlanId].TimeLayoutId));
            Assert.All(settings.RotationSteps, step =>
                Assert.Equal(customTimeLayoutId, step.TimeLayoutId));
            Assert.True(bridge.SaveCount > 0);
        });
    }

    [Fact]
    public void SelectingManagedTimeLayoutPreservesCustomBatchSelection()
    {
        WithService((settings, profile, _, service, customTimeLayoutId) =>
        {
            service.EnsureManagedSchedules();
            var customBatchTimeLayoutId = Guid.NewGuid();
            profile.TimeLayouts.Add(customBatchTimeLayoutId, new TimeLayout { Name = "第二批专用时间表" });
            settings.RotationSteps[1].TimeLayoutId = customBatchTimeLayoutId;

            Assert.True(service.SelectManagedTimeLayout(customTimeLayoutId));

            Assert.Equal(customTimeLayoutId, settings.RotationSteps[0].TimeLayoutId);
            Assert.Equal(customBatchTimeLayoutId, settings.RotationSteps[1].TimeLayoutId);
        });
    }

    private static void WithService(
        Action<CycleSettingsService, Profile, FakeScheduleControlBridge, CyclePlanService, Guid> action)
    {
        var root = Path.Combine(Path.GetTempPath(), $"cycle-plan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var settings = new CycleSettingsService(Path.Combine(root, "settings.json"));
            settings.CompleteOnboarding(true);
            settings.WorkDays = 1;

            var customTimeLayoutId = Guid.NewGuid();
            var sourcePlan = new ClassPlan
            {
                Name = "原课表",
                TimeLayoutId = customTimeLayoutId,
                IsEnabled = true
            };
            var profile = new Profile();
            profile.TimeLayouts.Add(customTimeLayoutId, new TimeLayout { Name = "用户自定义时间表" });
            profile.ClassPlans.Add(Guid.NewGuid(), sourcePlan);
            var bridge = new FakeScheduleControlBridge(profile) { CurrentClassPlan = sourcePlan };
            var service = new CyclePlanService(settings, bridge, new FakeExactTimeService());

            action(settings, profile, bridge, service, customTimeLayoutId);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class FakeScheduleControlBridge(Profile profile) : IScheduleControlBridge
    {
        public event EventHandler? PreMainTimerTicked
        {
            add { }
            remove { }
        }

        public bool IsClassPlanEnabled { get; set; }

        public bool IsClassPlanLoaded { get; set; }

        public ClassPlan? CurrentClassPlan { get; set; }

        public Profile Profile { get; } = profile;

        public int SaveCount { get; private set; }

        public void SaveProfile() => SaveCount++;
    }

    private sealed class FakeExactTimeService : IExactTimeService
    {
        public event PropertyChangedEventHandler? PropertyChanged
        {
            add { }
            remove { }
        }

        public event PropertyChangingEventHandler? PropertyChanging
        {
            add { }
            remove { }
        }

        public string SyncStatusMessage { get; set; } = "";

        public void Sync()
        {
        }

        public DateTime GetCurrentLocalDateTime() => new(2026, 7, 19, 8, 0, 0);
    }
}
