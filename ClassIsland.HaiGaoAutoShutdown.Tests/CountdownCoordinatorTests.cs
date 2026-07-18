using ClassIsland.HaiGaoAutoShutdown.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassIsland.HaiGaoAutoShutdown.Tests;

public sealed class CountdownCoordinatorTests
{
    [Fact]
    public async Task CancelledCountdown_DoesNotExecuteShutdown()
    {
        var display = new FakeDisplay { CancelOnFirstCountdown = true };
        var executor = new FakeShutdownExecutor();
        var coordinator = CreateCoordinator(display, executor);

        var outcome = await coordinator.RunScheduledAsync(60, CancellationToken.None);

        Assert.Equal(CountdownOutcome.Cancelled, outcome);
        Assert.Equal(0, executor.CallCount);
        Assert.True(display.Disposed);
    }

    [Fact]
    public async Task CompletedPreview_NeverExecutesShutdown()
    {
        var display = new FakeDisplay();
        var executor = new FakeShutdownExecutor();
        var coordinator = CreateCoordinator(display, executor);

        var outcome = await coordinator.RunPreviewAsync(1, CancellationToken.None);

        Assert.Equal(CountdownOutcome.PreviewCompleted, outcome);
        Assert.Equal(0, executor.CallCount);
        Assert.Contains("未执行关机", display.LastStatus);
    }

    [Fact]
    public async Task CompletedScheduledCountdown_ExecutesShutdownExactlyOnce()
    {
        var executor = new FakeShutdownExecutor();
        var coordinator = CreateCoordinator(new FakeDisplay(), executor);

        var outcome = await coordinator.RunScheduledAsync(1, CancellationToken.None);

        Assert.Equal(CountdownOutcome.ShutdownStarted, outcome);
        Assert.Equal(1, executor.CallCount);
    }

    [Theory]
    [InlineData(-10, 1)]
    [InlineData(1, 1)]
    [InlineData(3600, 3600)]
    [InlineData(4000, 3600)]
    public void CountdownBounds_AreNormalized(int input, int expected)
    {
        Assert.Equal(expected, CountdownLimits.Normalize(input));
    }

    private static CountdownCoordinator CreateCoordinator(
        FakeDisplay display,
        FakeShutdownExecutor executor) =>
        new(
            new FakeDisplayFactory(display),
            new FakeCountdownTiming(),
            executor,
            NullLogger<CountdownCoordinator>.Instance);

    private sealed class FakeDisplayFactory(FakeDisplay display) : ICountdownDisplayFactory
    {
        public Task<ICountdownDisplay?> CreateAsync(bool isPreview, CancellationToken cancellationToken) =>
            Task.FromResult<ICountdownDisplay?>(display);
    }

    private sealed class FakeDisplay : ICountdownDisplay
    {
        public event EventHandler? CancelRequested;

        public bool CancelOnFirstCountdown { get; init; }

        public bool Disposed { get; private set; }

        public string LastStatus { get; private set; } = string.Empty;

        public void SetCountdown(int remainingSeconds)
        {
            if (CancelOnFirstCountdown)
            {
                CancelRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        public void SetStatus(string message) => LastStatus = message;

        public void SetError(string message) => LastStatus = message;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeCountdownTiming : ICountdownTiming
    {
        private long _elapsedTicks;

        public long GetTimestamp() => _elapsedTicks;

        public TimeSpan GetElapsedTime(long startingTimestamp) =>
            TimeSpan.FromTicks(_elapsedTicks - startingTimestamp);

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _elapsedTicks += delay.Ticks;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeShutdownExecutor : IShutdownExecutor
    {
        public int CallCount { get; private set; }

        public Task<ShutdownExecutionResult> ExecuteAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new ShutdownExecutionResult(true));
        }
    }
}
