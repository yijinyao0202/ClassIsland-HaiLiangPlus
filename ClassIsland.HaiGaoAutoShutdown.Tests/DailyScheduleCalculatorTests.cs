using ClassIsland.HaiGaoAutoShutdown.Services;
using ClassIsland.HaiGaoAutoShutdown.Models;

namespace ClassIsland.HaiGaoAutoShutdown.Tests;

public sealed class DailyScheduleCalculatorTests
{
    private static readonly TimeSpan ScheduleTime = new(22, 0, 0);

    [Fact]
    public void BeforeSchedule_ReturnsToday()
    {
        var now = new DateTime(2026, 7, 18, 21, 59, 0);

        var result = DailyScheduleCalculator.GetNextOccurrence(now, ScheduleTime);

        Assert.Equal(new DateTime(2026, 7, 18, 22, 0, 0), result);
    }

    [Fact]
    public void AtOrAfterSchedule_ReturnsTomorrowWithoutCatchUp()
    {
        Assert.Equal(
            new DateTime(2026, 7, 19, 22, 0, 0),
            DailyScheduleCalculator.GetNextOccurrence(
                new DateTime(2026, 7, 18, 22, 0, 0),
                ScheduleTime));
        Assert.Equal(
            new DateTime(2026, 7, 19, 22, 0, 0),
            DailyScheduleCalculator.GetNextOccurrence(
                new DateTime(2026, 7, 18, 23, 30, 0),
                ScheduleTime));
    }

    [Fact]
    public void NormalCrossing_Triggers()
    {
        var result = DailyScheduleCalculator.EvaluateCrossing(
            new DateTime(2026, 7, 18, 21, 59, 59, 750),
            new DateTime(2026, 7, 18, 22, 0, 0, 100),
            new DateTime(2026, 7, 18, 22, 0, 0),
            TimeSpan.FromSeconds(2));

        Assert.Equal(ScheduleCrossingResult.Trigger, result);
    }

    [Fact]
    public void LongGap_SkipsMissedOccurrence()
    {
        var result = DailyScheduleCalculator.EvaluateCrossing(
            new DateTime(2026, 7, 18, 21, 50, 0),
            new DateTime(2026, 7, 18, 22, 10, 0),
            new DateTime(2026, 7, 18, 22, 0, 0),
            TimeSpan.FromSeconds(2));

        Assert.Equal(ScheduleCrossingResult.Skip, result);
    }

    [Fact]
    public void ClockMovedBackward_IsDetected()
    {
        var result = DailyScheduleCalculator.EvaluateCrossing(
            new DateTime(2026, 7, 18, 22, 0, 0),
            new DateTime(2026, 7, 18, 21, 0, 0),
            new DateTime(2026, 7, 19, 22, 0, 0),
            TimeSpan.FromSeconds(2));

        Assert.Equal(ScheduleCrossingResult.ClockMovedBackward, result);
    }

    [Fact]
    public void HandledOccurrenceAfterClockRollback_IsNotScheduledAgain()
    {
        var handled = new DateTime(2026, 7, 18, 22, 0, 0);
        var nowAfterRollback = new DateTime(2026, 7, 18, 21, 30, 0);

        var result = DailyScheduleCalculator.GetNextUnprocessedOccurrence(
            nowAfterRollback,
            ScheduleTime,
            handled);

        Assert.Equal(new DateTime(2026, 7, 19, 22, 0, 0), result);
    }

    [Fact]
    public void MultipleSchedules_ReturnNearestEnabledTime()
    {
        var schedules = new[]
        {
            CreateSchedule("午休", 12, 30),
            CreateSchedule("晚修", 22, 15)
        };

        var result = DailyScheduleCalculator.GetNextOccurrence(
            new DateTime(2026, 7, 18, 10, 0, 0),
            schedules,
            null);

        Assert.NotNull(result);
        Assert.Equal("午休", result.ScheduleName);
        Assert.Equal(new DateTime(2026, 7, 18, 12, 30, 0), result.OccursAt);
    }

    [Fact]
    public void AfterEarlierSchedule_ReturnsLaterScheduleOnSameDay()
    {
        var schedules = new[]
        {
            CreateSchedule("午休", 12, 30),
            CreateSchedule("晚修", 22, 15)
        };

        var result = DailyScheduleCalculator.GetNextOccurrence(
            new DateTime(2026, 7, 18, 12, 31, 0),
            schedules,
            new DateTime(2026, 7, 18, 12, 30, 0));

        Assert.NotNull(result);
        Assert.Equal("晚修", result.ScheduleName);
        Assert.Equal(new DateTime(2026, 7, 18, 22, 15, 0), result.OccursAt);
    }

    [Fact]
    public void AfterAllSchedules_ReturnsFirstScheduleTomorrow()
    {
        var schedules = new[]
        {
            CreateSchedule("午休", 12, 30),
            CreateSchedule("晚修", 22, 15)
        };

        var result = DailyScheduleCalculator.GetNextOccurrence(
            new DateTime(2026, 7, 18, 23, 0, 0),
            schedules,
            new DateTime(2026, 7, 18, 22, 15, 0));

        Assert.NotNull(result);
        Assert.Equal("午休", result.ScheduleName);
        Assert.Equal(new DateTime(2026, 7, 19, 12, 30, 0), result.OccursAt);
    }

    [Fact]
    public void ClockRollback_DoesNotRepeatSchedulesBeforeLastHandledTime()
    {
        var schedules = new[]
        {
            CreateSchedule("午休", 12, 30),
            CreateSchedule("晚修", 22, 15)
        };

        var result = DailyScheduleCalculator.GetNextOccurrence(
            new DateTime(2026, 7, 18, 11, 0, 0),
            schedules,
            new DateTime(2026, 7, 18, 13, 0, 0));

        Assert.NotNull(result);
        Assert.Equal("晚修", result.ScheduleName);
        Assert.Equal(new DateTime(2026, 7, 18, 22, 15, 0), result.OccursAt);
    }

    [Fact]
    public void NoSchedules_ReturnsNull()
    {
        Assert.Null(DailyScheduleCalculator.GetNextOccurrence(
            new DateTime(2026, 7, 18, 11, 0, 0),
            Array.Empty<AutoShutdownScheduleSnapshot>(),
            null));
    }

    [Fact]
    public void LatestOccurrence_CoversAllSchedulesMissedDuringLongGap()
    {
        var schedules = new[]
        {
            CreateSchedule("午休", 12, 30),
            CreateSchedule("晚修", 22, 15)
        };

        var result = DailyScheduleCalculator.GetLatestOccurrenceAtOrBefore(
            new DateTime(2026, 7, 18, 23, 0, 0),
            schedules);

        Assert.NotNull(result);
        Assert.Equal("晚修", result.ScheduleName);
        Assert.Equal(new DateTime(2026, 7, 18, 22, 15, 0), result.OccursAt);
    }

    private static AutoShutdownScheduleSnapshot CreateSchedule(string name, int hour, int minute) =>
        new(Guid.NewGuid(), name, new TimeSpan(hour, minute, 0));
}
