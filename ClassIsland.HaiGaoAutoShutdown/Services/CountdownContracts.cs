using System.Diagnostics;

namespace ClassIsland.HaiGaoAutoShutdown.Services;

public enum CountdownOutcome
{
    Cancelled,
    ShutdownStarted,
    PreviewCompleted,
    PresentationFailed,
    ShutdownFailed,
    AlreadyRunning
}

public interface ICountdownDisplay : IAsyncDisposable
{
    event EventHandler? CancelRequested;

    void SetCountdown(int remainingSeconds);

    void SetStatus(string message);

    void SetError(string message);
}

public interface ICountdownDisplayFactory
{
    Task<ICountdownDisplay?> CreateAsync(bool isPreview, CancellationToken cancellationToken);
}

public interface ICountdownTiming
{
    long GetTimestamp();

    TimeSpan GetElapsedTime(long startingTimestamp);

    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed record ShutdownExecutionResult(bool Success, string? ErrorMessage = null, int? ExitCode = null);

public interface IShutdownExecutor
{
    Task<ShutdownExecutionResult> ExecuteAsync(CancellationToken cancellationToken);
}

public sealed class SystemCountdownTiming : ICountdownTiming
{
    public long GetTimestamp() => Stopwatch.GetTimestamp();

    public TimeSpan GetElapsedTime(long startingTimestamp) => Stopwatch.GetElapsedTime(startingTimestamp);

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}
