using ClassIsland.HaiGaoAutoShutdown.Services;

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
}
