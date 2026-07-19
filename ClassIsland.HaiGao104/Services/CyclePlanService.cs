using System.Text.Json;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.HaiGao104.Models;
using ClassIsland.Shared.Models.Profile;

namespace ClassIsland.HaiGao104.Services;

public sealed class CyclePlanService(
    CycleSettingsService settings,
    IScheduleControlBridge scheduleBridge,
    IExactTimeService exactTimeService)
{
    private const string ManagedTimeLayoutName = "海亮教育+基础时间表";
    private bool _isEnsuring;

    public IReadOnlyList<Guid> EnsureManagedSchedules()
    {
        if (_isEnsuring)
        {
            return settings.ManagedClassPlanIds.ToArray();
        }

        _isEnsuring = true;
        try
        {
            var profile = scheduleBridge.Profile;
            var targetCount = Math.Clamp(settings.WorkDays, 1, 100);
            var managedIds = settings.ManagedClassPlanIds.Take(targetCount).ToList();
            var archivedPlans = settings.ArchivedCyclePlans.ToList();
            var changed = false;

            foreach (var removedId in settings.ManagedClassPlanIds.Skip(targetCount))
            {
                if (removedId == Guid.Empty ||
                    !profile.ClassPlans.TryGetValue(removedId, out var removedPlan) ||
                    archivedPlans.Any(item => item.ClassPlanId == removedId))
                {
                    continue;
                }

                archivedPlans.Add(new ArchivedCyclePlan(
                    removedId,
                    targetCount,
                    removedPlan.Name,
                    exactTimeService.GetCurrentLocalDateTime()));
            }

            while (managedIds.Count < targetCount)
            {
                managedIds.Add(Guid.Empty);
            }

            var validManagedIds = managedIds
                .Where(id => id != Guid.Empty &&
                             profile.ClassPlans.TryGetValue(id, out var plan) &&
                             !plan.IsOverlay)
                .Distinct()
                .ToList();
            var sourcePlan = validManagedIds.Count > 0
                ? profile.ClassPlans[validManagedIds[0]]
                : FindSourcePlan(profile, archivedPlans);
            var timeLayoutId = EnsureManagedTimeLayout(profile, validManagedIds, sourcePlan, ref changed);
            var activeTimeLayoutId = EnsureRotationTimeLayouts(profile, timeLayoutId, ref changed);
            var templatePlan = sourcePlan;
            var seenIds = new HashSet<Guid>();

            for (var index = 0; index < targetCount; index++)
            {
                var managedId = managedIds[index];
                if (managedId != Guid.Empty &&
                    seenIds.Add(managedId) &&
                    profile.ClassPlans.TryGetValue(managedId, out var existingPlan) &&
                    !existingPlan.IsOverlay)
                {
                    templatePlan ??= existingPlan;
                    continue;
                }

                var newPlan = templatePlan is null ? new ClassPlan() : Duplicate(templatePlan);
                var newPlanId = Guid.NewGuid();
                profile.ClassPlans.Add(newPlanId, newPlan);
                managedIds[index] = newPlanId;
                seenIds.Add(newPlanId);
                templatePlan ??= newPlan;
                changed = true;
            }

            for (var index = 0; index < managedIds.Count; index++)
            {
                changed |= ConfigureManagedPlan(profile.ClassPlans[managedIds[index]], index + 1, activeTimeLayoutId);
            }

            archivedPlans = archivedPlans
                .Where(item => item.ClassPlanId != Guid.Empty &&
                               !managedIds.Contains(item.ClassPlanId) &&
                               profile.ClassPlans.ContainsKey(item.ClassPlanId))
                .DistinctBy(item => item.ClassPlanId)
                .ToList();
            foreach (var archivedPlan in archivedPlans)
            {
                changed |= ConfigureArchivedPlan(profile.ClassPlans[archivedPlan.ClassPlanId], archivedPlan, timeLayoutId);
            }

            if (changed)
            {
                scheduleBridge.SaveProfile();
            }
            settings.SetManagedScheduleState(managedIds, timeLayoutId, archivedPlans);
            return managedIds.ToArray();
        }
        finally
        {
            _isEnsuring = false;
        }
    }

    public Guid? DuplicateRotationTimeLayout(RotationStep step)
    {
        EnsureManagedSchedules();
        var profile = scheduleBridge.Profile;
        var sourceId = step.TimeLayoutId ?? settings.ManagedTimeLayoutId;
        if (sourceId is not { } validSourceId ||
            !profile.TimeLayouts.TryGetValue(validSourceId, out var sourceLayout))
        {
            return null;
        }

        var duplicatedLayout = Duplicate(sourceLayout);
        var batchName = string.IsNullOrWhiteSpace(step.Name) ? "班级批次" : step.Name.Trim();
        duplicatedLayout.Name = GetUniqueTimeLayoutName(profile, $"{batchName}时间表");
        var duplicatedLayoutId = Guid.NewGuid();
        profile.TimeLayouts.Add(duplicatedLayoutId, duplicatedLayout);
        step.TimeLayoutId = duplicatedLayoutId;
        scheduleBridge.SaveProfile();
        return duplicatedLayoutId;
    }

    public bool SelectManagedTimeLayout(Guid timeLayoutId)
    {
        var managedIds = EnsureManagedSchedules().ToArray();
        var profile = scheduleBridge.Profile;
        if (!profile.TimeLayouts.ContainsKey(timeLayoutId))
        {
            return false;
        }

        var previousTimeLayoutId = settings.ManagedTimeLayoutId;
        if (previousTimeLayoutId == timeLayoutId)
        {
            return true;
        }

        foreach (var step in settings.RotationSteps.Where(step =>
                     step.TimeLayoutId is null || step.TimeLayoutId == previousTimeLayoutId))
        {
            step.TimeLayoutId = timeLayoutId;
        }

        settings.SetManagedScheduleState(managedIds, timeLayoutId, settings.ArchivedCyclePlans);
        EnsureManagedSchedules();
        return true;
    }

    public bool InsertDay(int insertIndex)
    {
        var managedIds = EnsureManagedSchedules().ToList();
        if (managedIds.Count >= 100)
        {
            return false;
        }

        var profile = scheduleBridge.Profile;
        var normalizedIndex = Math.Clamp(insertIndex, 0, managedIds.Count);
        var sourceIndex = normalizedIndex == 0 ? 0 : normalizedIndex - 1;
        var newPlan = Duplicate(profile.ClassPlans[managedIds[sourceIndex]]);
        var newPlanId = Guid.NewGuid();
        var snapshot = CaptureMutationState(managedIds);

        profile.ClassPlans.Add(newPlanId, newPlan);
        managedIds.Insert(normalizedIndex, newPlanId);
        ConfigureActivePlans(profile, managedIds);
        scheduleBridge.SaveProfile();
        ApplyMutation(managedIds, settings.ArchivedCyclePlans.ToList(), snapshot);
        return true;
    }

    public bool ArchiveDay(int dayIndex)
    {
        var managedIds = EnsureManagedSchedules().ToList();
        if (managedIds.Count <= 1 || dayIndex < 0 || dayIndex >= managedIds.Count)
        {
            return false;
        }

        var profile = scheduleBridge.Profile;
        var snapshot = CaptureMutationState(managedIds);
        var archivedId = managedIds[dayIndex];
        var archivedPlan = profile.ClassPlans[archivedId];
        var archives = settings.ArchivedCyclePlans
            .Where(item => item.ClassPlanId != archivedId)
            .ToList();
        var archive = new ArchivedCyclePlan(
            archivedId,
            dayIndex,
            archivedPlan.Name,
            exactTimeService.GetCurrentLocalDateTime());

        archives.Add(archive);
        managedIds.RemoveAt(dayIndex);
        ConfigureActivePlans(profile, managedIds);
        ConfigureArchivedPlan(archivedPlan, archive, GetManagedTimeLayoutId());
        scheduleBridge.SaveProfile();
        ApplyMutation(managedIds, archives, snapshot);
        return true;
    }

    public bool RestoreArchived(Guid classPlanId)
    {
        var managedIds = EnsureManagedSchedules().ToList();
        if (managedIds.Count >= 100)
        {
            return false;
        }

        var profile = scheduleBridge.Profile;
        var archive = settings.ArchivedCyclePlans.FirstOrDefault(item => item.ClassPlanId == classPlanId);
        if (archive is null || !profile.ClassPlans.ContainsKey(classPlanId) || managedIds.Contains(classPlanId))
        {
            return false;
        }

        var snapshot = CaptureMutationState(managedIds);
        var insertIndex = Math.Clamp(archive.OriginalIndex, 0, managedIds.Count);
        managedIds.Insert(insertIndex, classPlanId);
        var archives = settings.ArchivedCyclePlans
            .Where(item => item.ClassPlanId != classPlanId)
            .ToList();

        ConfigureActivePlans(profile, managedIds);
        scheduleBridge.SaveProfile();
        ApplyMutation(managedIds, archives, snapshot);
        return true;
    }

    private MutationSnapshot CaptureMutationState(IReadOnlyList<Guid> managedIds)
    {
        var today = exactTimeService.GetCurrentLocalDateTime().Date;
        var cyclePosition = settings.GetCyclePosition(today);
        var isNominalRestDay = cyclePosition >= managedIds.Count;
        Guid? currentPlanId = isNominalRestDay ? null : managedIds[cyclePosition];
        var restOrdinal = isNominalRestDay ? cyclePosition - managedIds.Count + 1 : 0;
        var currentWorkIndex = isNominalRestDay ? -1 : cyclePosition;
        Guid? repeatStartPlanId = null;
        var repeatStartIndex = Math.Clamp(settings.RestRepeatStartDay - 1, 0, managedIds.Count - 1);
        if (settings.RestRepeatStartDate is not null && managedIds.Count > 0)
        {
            repeatStartPlanId = managedIds[repeatStartIndex];
        }

        return new MutationSnapshot(
            today,
            isNominalRestDay,
            currentPlanId,
            currentWorkIndex,
            restOrdinal,
            repeatStartPlanId,
            repeatStartIndex);
    }

    private void ApplyMutation(
        IReadOnlyList<Guid> managedIds,
        IReadOnlyList<ArchivedCyclePlan> archives,
        MutationSnapshot snapshot)
    {
        var managedIdList = managedIds as IList<Guid> ?? managedIds.ToList();
        int todayCycleDay;
        if (snapshot.IsNominalRestDay)
        {
            todayCycleDay = managedIds.Count + snapshot.RestOrdinal;
        }
        else if (snapshot.CurrentPlanId is { } currentPlanId && managedIdList.IndexOf(currentPlanId) is var currentIndex && currentIndex >= 0)
        {
            todayCycleDay = currentIndex + 1;
        }
        else
        {
            todayCycleDay = Math.Clamp(snapshot.CurrentWorkIndex, 0, managedIds.Count - 1) + 1;
        }

        int? repeatStartDay = null;
        if (settings.RestRepeatStartDate is not null)
        {
            if (snapshot.RepeatStartPlanId is { } repeatPlanId && managedIdList.IndexOf(repeatPlanId) is var repeatIndex && repeatIndex >= 0)
            {
                repeatStartDay = repeatIndex + 1;
            }
            else
            {
                repeatStartDay = Math.Clamp(snapshot.RepeatStartIndex, 0, managedIds.Count - 1) + 1;
            }
        }

        settings.ApplyCyclePlanMutation(managedIds, archives, snapshot.Today, todayCycleDay, repeatStartDay);
    }

    private void ConfigureActivePlans(Profile profile, IReadOnlyList<Guid> managedIds)
    {
        var timeLayoutId = GetManagedTimeLayoutId();
        for (var index = 0; index < managedIds.Count; index++)
        {
            ConfigureManagedPlan(profile.ClassPlans[managedIds[index]], index + 1, timeLayoutId);
        }
    }

    private Guid GetManagedTimeLayoutId() => settings.ManagedTimeLayoutId
        ?? throw new InvalidOperationException("海高托管时间表尚未创建。");

    private ClassPlan? FindSourcePlan(Profile profile, IReadOnlyList<ArchivedCyclePlan> archivedPlans)
    {
        return profile.ClassPlans
                   .Where(item => !item.Value.IsOverlay && item.Key == settings.FixedClassPlanId)
                   .Select(item => item.Value)
                   .FirstOrDefault()
               ?? archivedPlans
                   .Where(item => profile.ClassPlans.ContainsKey(item.ClassPlanId))
                   .Select(item => profile.ClassPlans[item.ClassPlanId])
                   .FirstOrDefault(item => !item.IsOverlay)
               ?? profile.ClassPlans
                   .Where(item => !item.Value.IsOverlay && ReferenceEquals(item.Value, scheduleBridge.CurrentClassPlan))
                   .Select(item => item.Value)
                   .FirstOrDefault()
               ?? profile.ClassPlans
                   .Where(item => !item.Value.IsOverlay && item.Value.IsEnabled)
                   .Select(item => item.Value)
                   .FirstOrDefault()
               ?? profile.ClassPlans
                   .Where(item => !item.Value.IsOverlay)
                   .Select(item => item.Value)
                   .FirstOrDefault();
    }

    private Guid EnsureManagedTimeLayout(
        Profile profile,
        IReadOnlyList<Guid> managedIds,
        ClassPlan? sourcePlan,
        ref bool changed)
    {
        if (settings.ManagedTimeLayoutId is { } configuredTimeLayoutId &&
            profile.TimeLayouts.ContainsKey(configuredTimeLayoutId))
        {
            return configuredTimeLayoutId;
        }

        foreach (var managedId in managedIds)
        {
            var existingTimeLayoutId = profile.ClassPlans[managedId].TimeLayoutId;
            if (!profile.TimeLayouts.ContainsKey(existingTimeLayoutId))
            {
                continue;
            }

            return existingTimeLayoutId;
        }

        var sourceTimeLayout = sourcePlan is not null &&
                               profile.TimeLayouts.TryGetValue(sourcePlan.TimeLayoutId, out var value)
            ? value
            : null;
        var managedTimeLayout = sourceTimeLayout is null ? new TimeLayout() : Duplicate(sourceTimeLayout);
        managedTimeLayout.Name = GetUniqueTimeLayoutName(profile, ManagedTimeLayoutName);
        var timeLayoutId = Guid.NewGuid();
        profile.TimeLayouts.Add(timeLayoutId, managedTimeLayout);
        changed = true;
        return timeLayoutId;
    }

    private Guid EnsureRotationTimeLayouts(Profile profile, Guid baseTimeLayoutId, ref bool changed)
    {
        if (!profile.TimeLayouts.TryGetValue(baseTimeLayoutId, out var baseTimeLayout))
        {
            return baseTimeLayoutId;
        }

        for (var index = 0; index < settings.RotationSteps.Count; index++)
        {
            var step = settings.RotationSteps[index];
            var batchName = string.IsNullOrWhiteSpace(step.Name) ? $"第 {index + 1} 批" : step.Name.Trim();
            if (step.TimeLayoutId is { } configuredId &&
                profile.TimeLayouts.TryGetValue(configuredId, out var configuredLayout))
            {
                var legacyName = $"海亮周 - {batchName}时间表";
                if (configuredLayout.Name == legacyName)
                {
                    configuredLayout.Name = $"海亮教育+ - {batchName}时间表";
                    changed = true;
                }
                continue;
            }

            step.TimeLayoutId = baseTimeLayoutId;
        }

        if (!settings.IsRotationEnabled)
        {
            return baseTimeLayoutId;
        }

        var today = exactTimeService.GetCurrentLocalDateTime();
        return settings.GetRotationStep(today)?.TimeLayoutId is { } activeId &&
               profile.TimeLayouts.ContainsKey(activeId)
            ? activeId
            : baseTimeLayoutId;
    }

    private static string GetUniqueTimeLayoutName(Profile profile, string baseName)
    {
        var existingNames = profile.TimeLayouts.Values
            .Select(timeLayout => timeLayout.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existingNames.Contains(baseName))
        {
            return baseName;
        }

        for (var number = 2; ; number++)
        {
            var candidate = $"{baseName} ({number})";
            if (!existingNames.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static bool ConfigureManagedPlan(ClassPlan plan, int dayNumber, Guid timeLayoutId)
    {
        var changed = false;
        var name = CycleDayNameFormatter.GetName(dayNumber);
        if (plan.Name != name)
        {
            plan.Name = name;
            changed = true;
        }
        if (plan.IsOverlay)
        {
            plan.IsOverlay = false;
            changed = true;
        }
        if (plan.OverlaySourceId is not null)
        {
            plan.OverlaySourceId = null;
            changed = true;
        }
        if (plan.IsEnabled)
        {
            plan.IsEnabled = false;
            changed = true;
        }
        if (plan.TimeLayoutId != timeLayoutId)
        {
            plan.TimeLayoutId = timeLayoutId;
            changed = true;
        }
        return changed;
    }

    private static bool ConfigureArchivedPlan(
        ClassPlan plan,
        ArchivedCyclePlan archive,
        Guid timeLayoutId)
    {
        var changed = false;
        var name = $"[归档] {archive.OriginalName}";
        if (plan.Name != name)
        {
            plan.Name = name;
            changed = true;
        }
        if (plan.IsOverlay)
        {
            plan.IsOverlay = false;
            changed = true;
        }
        if (plan.OverlaySourceId is not null)
        {
            plan.OverlaySourceId = null;
            changed = true;
        }
        if (plan.IsEnabled)
        {
            plan.IsEnabled = false;
            changed = true;
        }
        if (plan.TimeLayoutId != timeLayoutId)
        {
            plan.TimeLayoutId = timeLayoutId;
            changed = true;
        }
        return changed;
    }

    private static T Duplicate<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))
        ?? throw new InvalidOperationException($"无法复制 {typeof(T).Name}。");

    private sealed record MutationSnapshot(
        DateTime Today,
        bool IsNominalRestDay,
        Guid? CurrentPlanId,
        int CurrentWorkIndex,
        int RestOrdinal,
        Guid? RepeatStartPlanId,
        int RepeatStartIndex);
}
