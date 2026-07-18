using Microsoft.Extensions.Logging;

namespace ClassIsland.HaiGaoAutoShutdown.Services;

public sealed class CountdownCoordinator(
    ICountdownDisplayFactory displayFactory,
    ICountdownTiming timing,
    IShutdownExecutor shutdownExecutor,
    ILogger<CountdownCoordinator> logger)
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(100);
    private readonly object _activeGate = new();
    private CancellationTokenSource? _activeCancellationSource;
    private bool _activeIsPreview;
    private int _isRunning;

    public Task<CountdownOutcome> RunScheduledAsync(int seconds, CancellationToken cancellationToken) =>
        RunAsync(CountdownLimits.Normalize(seconds), false, cancellationToken);

    public Task<CountdownOutcome> RunPreviewAsync(int seconds, CancellationToken cancellationToken) =>
        RunAsync(CountdownLimits.Normalize(seconds), true, cancellationToken);

    public void CancelScheduled()
    {
        lock (_activeGate)
        {
            if (!_activeIsPreview)
            {
                _activeCancellationSource?.Cancel();
            }
        }
    }

    public void CancelActive()
    {
        lock (_activeGate)
        {
            _activeCancellationSource?.Cancel();
        }
    }

    private async Task<CountdownOutcome> RunAsync(
        int seconds,
        bool isPreview,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
        {
            return CountdownOutcome.AlreadyRunning;
        }

        using var linkedCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_activeGate)
        {
            _activeCancellationSource = linkedCancellationSource;
            _activeIsPreview = isPreview;
        }

        ICountdownDisplay? display = null;
        try
        {
            display = await displayFactory.CreateAsync(isPreview, linkedCancellationSource.Token);
            if (display is null)
            {
                logger.LogError("无法在所有屏幕上创建定时关机倒计时窗口，本次关机已取消。");
                return CountdownOutcome.PresentationFailed;
            }

            var sessionState = 0; // 0=倒计时，1=正在执行，2=已取消，3=错误待关闭
            var cancelSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnCancelRequested(object? sender, EventArgs args)
            {
                if (Interlocked.CompareExchange(ref sessionState, 2, 0) == 0 ||
                    Volatile.Read(ref sessionState) == 3)
                {
                    cancelSignal.TrySetResult();
                }
            }

            display.CancelRequested += OnCancelRequested;
            try
            {
                var startTimestamp = timing.GetTimestamp();
                while (true)
                {
                    linkedCancellationSource.Token.ThrowIfCancellationRequested();
                    if (Volatile.Read(ref sessionState) == 2)
                    {
                        return CountdownOutcome.Cancelled;
                    }

                    var elapsed = timing.GetElapsedTime(startTimestamp);
                    var remaining = (int)Math.Ceiling(seconds - elapsed.TotalSeconds);
                    if (remaining <= 0)
                    {
                        break;
                    }

                    display.SetCountdown(remaining);
                    var delayTask = timing.DelayAsync(RefreshInterval, linkedCancellationSource.Token);
                    var completedTask = await Task.WhenAny(delayTask, cancelSignal.Task);
                    if (completedTask == cancelSignal.Task &&
                        Volatile.Read(ref sessionState) == 2)
                    {
                        return CountdownOutcome.Cancelled;
                    }
                    await delayTask;
                }

                if (Interlocked.CompareExchange(ref sessionState, 1, 0) != 0)
                {
                    return CountdownOutcome.Cancelled;
                }

                if (isPreview)
                {
                    display.SetStatus("安全预览完成，未执行关机命令。");
                    await timing.DelayAsync(TimeSpan.FromMilliseconds(750), linkedCancellationSource.Token);
                    return CountdownOutcome.PreviewCompleted;
                }

                display.SetStatus("正在请求 Windows 正常关机……");
                var result = await shutdownExecutor.ExecuteAsync(linkedCancellationSource.Token);
                if (result.Success)
                {
                    return CountdownOutcome.ShutdownStarted;
                }

                Volatile.Write(ref sessionState, 3);
                var errorMessage = string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? "关机命令执行失败。点击屏幕任意处关闭提示。"
                    : $"关机命令执行失败：{result.ErrorMessage}\n点击屏幕任意处关闭提示。";
                display.SetError(errorMessage);
                await cancelSignal.Task.WaitAsync(linkedCancellationSource.Token);
                return CountdownOutcome.ShutdownFailed;
            }
            finally
            {
                display.CancelRequested -= OnCancelRequested;
            }
        }
        catch (OperationCanceledException)
        {
            return CountdownOutcome.Cancelled;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "运行定时关机倒计时失败，本次关机已取消。");
            return CountdownOutcome.PresentationFailed;
        }
        finally
        {
            if (display is not null)
            {
                try
                {
                    await display.DisposeAsync();
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "关闭定时关机倒计时窗口失败。");
                }
            }

            lock (_activeGate)
            {
                if (ReferenceEquals(_activeCancellationSource, linkedCancellationSource))
                {
                    _activeCancellationSource = null;
                    _activeIsPreview = false;
                }
            }
            Volatile.Write(ref _isRunning, 0);
        }
    }
}
