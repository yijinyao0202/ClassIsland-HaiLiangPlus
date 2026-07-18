namespace ClassIsland.HaiGaoAutoShutdown.Services;

public enum ScheduleCrossingResult
{
    None,
    Trigger,
    Skip,
    ClockMovedBackward
}

public static class DailyScheduleCalculator
{
    public static DateTime GetNextOccurrence(DateTime now, TimeSpan shutdownTime)
    {
        var normalizedTime = NormalizeTime(shutdownTime);
        var candidate = now.Date + normalizedTime;
        return candidate > now ? candidate : candidate.AddDays(1);
    }

    public static DateTime GetNextUnprocessedOccurrence(
        DateTime now,
        TimeSpan shutdownTime,
        DateTime? lastHandledOccurrence)
    {
        var candidate = GetNextOccurrence(now, shutdownTime);
        if (lastHandledOccurrence is not null && candidate == lastHandledOccurrence.Value)
        {
            candidate = candidate.AddDays(1);
        }
        return candidate;
    }

    public static DateTime GetLatestOccurrenceAtOrBefore(DateTime now, TimeSpan shutdownTime)
    {
        var candidate = now.Date + NormalizeTime(shutdownTime);
        return candidate <= now ? candidate : candidate.AddDays(-1);
    }

    public static ScheduleCrossingResult EvaluateCrossing(
        DateTime previousSample,
        DateTime currentSample,
        DateTime nextOccurrence,
        TimeSpan maximumContinuousGap)
    {
        if (currentSample < previousSample)
        {
            return ScheduleCrossingResult.ClockMovedBackward;
        }
        if (currentSample < nextOccurrence)
        {
            return ScheduleCrossingResult.None;
        }

        var sampleGap = currentSample - previousSample;
        var triggerLag = currentSample - nextOccurrence;
        return sampleGap <= maximumContinuousGap && triggerLag <= maximumContinuousGap
            ? ScheduleCrossingResult.Trigger
            : ScheduleCrossingResult.Skip;
    }

    private static TimeSpan NormalizeTime(TimeSpan time) =>
        time >= TimeSpan.Zero && time < TimeSpan.FromDays(1)
            ? new TimeSpan(time.Hours, time.Minutes, 0)
            : new TimeSpan(22, 0, 0);
}
