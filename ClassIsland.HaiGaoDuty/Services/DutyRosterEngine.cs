using ClassIsland.HaiGaoDuty.Models;

namespace ClassIsland.HaiGaoDuty.Services;

internal sealed class DutyRosterEngine(IShuffleSource shuffleSource)
{
    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    public DutyDayAssignment AssignNextDutyDay(DutyRosterPersistentState state)
    {
        if (state.ActiveConfiguration is not { Names.Count: > 0 } active)
        {
            return new DutyDayAssignment([], "尚未配置值日生名单");
        }

        PrepareCycleAtDayBoundary(state);
        active = state.ActiveConfiguration!;
        var targetCount = active.DailyCount;
        var selected = new List<string>(targetCount);

        while (selected.Count < targetCount)
        {
            if (state.CurrentOrderCursor >= state.CurrentOrder.Count)
            {
                StartNextCycle(state, selected);
                var remainingNeeded = targetCount - selected.Count;
                var availableWithoutDailyDuplicates = state.CurrentOrder
                    .Count(name => !selected.Contains(name, NameComparer));
                if (availableWithoutDailyDuplicates < remainingNeeded)
                {
                    return new DutyDayAssignment(selected,
                        $"新一轮可用名单不足，今日仅安排 {selected.Count}/{targetCount} 人");
                }
            }

            var name = state.CurrentOrder[state.CurrentOrderCursor++];
            if (selected.Contains(name, NameComparer))
            {
                return new DutyDayAssignment(selected,
                    $"新一轮无法在同一天排除重复姓名，今日仅安排 {selected.Count}/{targetCount} 人");
            }
            selected.Add(name);
        }

        return new DutyDayAssignment(selected, null);
    }

    public void ActivateInitialConfiguration(
        DutyRosterPersistentState state,
        DutyRosterConfiguration configuration)
    {
        state.ActiveConfiguration = configuration.Clone();
        state.PendingConfiguration = null;
        state.CurrentOrder = [];
        state.CurrentOrderCursor = 0;
        state.CycleNumber = 0;
        state.AssignmentDate = null;
        state.TodayAssignees = [];
    }

    public string? ValidatePendingConfiguration(
        DutyRosterPersistentState state,
        DutyRosterConfiguration configuration)
    {
        var generalError = ValidateConfiguration(configuration);
        if (generalError is not null)
        {
            return generalError;
        }

        if (state.ActiveConfiguration is null || state.CurrentOrder.Count == 0)
        {
            return null;
        }

        var remaining = Math.Max(0, state.CurrentOrder.Count - state.CurrentOrderCursor);
        var currentDailyCount = state.ActiveConfiguration.DailyCount;
        var tailCount = remaining % currentDailyCount;
        if (tailCount == 0)
        {
            return null;
        }

        var oldCycleTail = state.CurrentOrder
            .Skip(state.CurrentOrder.Count - tailCount)
            .ToHashSet(NameComparer);
        var requiredFromNewConfiguration = currentDailyCount - tailCount;
        var availableFromNewConfiguration = configuration.Names.Count(name => !oldCycleTail.Contains(name));

        return availableFromNewConfiguration < requiredFromNewConfiguration
            ? $"当前轮次结束当天还需从新名单补足 {requiredFromNewConfiguration} 人，" +
              $"但排除当天已安排人员后仅剩 {availableFromNewConfiguration} 人。请增加名单或等待轮次推进后再修改。"
            : null;
    }

    public static string? ValidateConfiguration(DutyRosterConfiguration configuration)
    {
        if (configuration.Names.Count == 0)
        {
            return "请至少录入一名值日生。";
        }

        if (configuration.Names.Any(name =>
                string.IsNullOrWhiteSpace(name) || !string.Equals(name, name.Trim(), StringComparison.Ordinal)))
        {
            return "名单不能包含空姓名，姓名两端也不能保留空格。";
        }

        if (configuration.DailyCount < 1)
        {
            return "每日人数必须大于 0。";
        }

        if (configuration.DailyCount > configuration.Names.Count)
        {
            return $"每日人数不能超过有效名单人数（{configuration.Names.Count} 人）。";
        }

        var duplicate = configuration.Names
            .GroupBy(name => name, NameComparer)
            .FirstOrDefault(group => group.Count() > 1);
        return duplicate is null
            ? null
            : $"名单中存在重复姓名“{duplicate.Key}”；同名学生请添加区分后缀。";
    }

    public static RosterParseResult ParseRoster(string? rosterText)
    {
        var names = (rosterText ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();
        var duplicate = names
            .GroupBy(name => name, NameComparer)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        return new RosterParseResult(names, duplicate);
    }

    public static bool ConfigurationEquals(
        DutyRosterConfiguration? left,
        DutyRosterConfiguration? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        return left is not null &&
               right is not null &&
               left.DailyCount == right.DailyCount &&
               left.Names.SequenceEqual(right.Names, NameComparer);
    }

    private void PrepareCycleAtDayBoundary(DutyRosterPersistentState state)
    {
        if (state.CurrentOrderCursor < state.CurrentOrder.Count)
        {
            return;
        }

        StartNextCycle(state, []);
    }

    private void StartNextCycle(DutyRosterPersistentState state, IReadOnlyCollection<string> selectedToday)
    {
        if (state.CurrentOrderCursor >= state.CurrentOrder.Count &&
            state.PendingConfiguration is not null)
        {
            state.ActiveConfiguration = state.PendingConfiguration;
            state.PendingConfiguration = null;
            state.CycleNumber = 0;
        }

        var names = state.ActiveConfiguration?.Names ?? [];
        state.CurrentOrder = Shuffle(names);
        if (selectedToday.Count > 0)
        {
            var selectedSet = selectedToday.ToHashSet(NameComparer);
            state.CurrentOrder =
            [
                .. state.CurrentOrder.Where(name => !selectedSet.Contains(name)),
                .. state.CurrentOrder.Where(selectedSet.Contains)
            ];
        }

        state.CurrentOrderCursor = 0;
        state.CycleNumber++;
    }

    private List<string> Shuffle(IReadOnlyList<string> names)
    {
        var order = names.ToList();
        for (var i = order.Count - 1; i > 0; i--)
        {
            var j = shuffleSource.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        return order;
    }
}

internal sealed record DutyDayAssignment(IReadOnlyList<string> Names, string? Warning);

internal sealed record RosterParseResult(IReadOnlyList<string> Names, string? DuplicateName);
