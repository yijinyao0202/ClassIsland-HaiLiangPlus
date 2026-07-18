using Avalonia.Controls;
using Avalonia.Threading;
using ClassIsland.Core;
using ClassIsland.HaiGaoAutoShutdown.Views;
using Microsoft.Extensions.Logging;

namespace ClassIsland.HaiGaoAutoShutdown.Services;

public sealed class MultiScreenCountdownDisplayFactory(
    ILogger<MultiScreenCountdownDisplayFactory> logger) : ICountdownDisplayFactory
{
    public async Task<ICountdownDisplay?> CreateAsync(bool isPreview, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Dispatcher.UIThread.InvokeAsync<ICountdownDisplay?>(() => CreateOnUiThread(isPreview));
    }

    private ICountdownDisplay? CreateOnUiThread(bool isPreview)
    {
        var windows = new List<ShutdownCountdownWindow>();
        try
        {
            var rootWindow = AppBase.Current.GetRootWindow();
            var screens = rootWindow.Screens.All;
            if (screens.Count == 0)
            {
                logger.LogError("Avalonia 没有返回任何显示器，无法显示定时关机提示。");
                return null;
            }

            foreach (var screen in screens)
            {
                var window = new ShutdownCountdownWindow(isPreview);
                var bounds = screen.Bounds;
                window.Width = bounds.Width / screen.Scaling;
                window.Height = bounds.Height / screen.Scaling;
                window.Position = bounds.Position;
                windows.Add(window);
            }

            foreach (var window in windows)
            {
                window.Show();
                window.Topmost = true;
            }
            windows[^1].Activate();
            return new MultiScreenCountdownDisplay(windows);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "创建多屏定时关机提示失败，本次关机将被取消。");
            foreach (var window in windows)
            {
                window.Close();
            }
            return null;
        }
    }

    private sealed class MultiScreenCountdownDisplay : ICountdownDisplay
    {
        private readonly IReadOnlyList<ShutdownCountdownWindow> _windows;
        private int _isDisposed;

        public MultiScreenCountdownDisplay(IReadOnlyList<ShutdownCountdownWindow> windows)
        {
            _windows = windows;
            foreach (var window in _windows)
            {
                window.CancelRequested += WindowOnCancelRequested;
            }
        }

        public event EventHandler? CancelRequested;

        public void SetCountdown(int remainingSeconds) =>
            PostToWindows(window => window.SetCountdown(remainingSeconds));

        public void SetStatus(string message) =>
            PostToWindows(window => window.SetStatus(message));

        public void SetError(string message) =>
            PostToWindows(window => window.SetError(message));

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var window in _windows)
                {
                    window.CancelRequested -= WindowOnCancelRequested;
                    window.Close();
                }
            });
        }

        private void WindowOnCancelRequested(object? sender, EventArgs args) =>
            CancelRequested?.Invoke(this, EventArgs.Empty);

        private void PostToWindows(Action<ShutdownCountdownWindow> action)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (Volatile.Read(ref _isDisposed) != 0)
                {
                    return;
                }
                foreach (var window in _windows)
                {
                    action(window);
                }
            }, DispatcherPriority.Send);
        }
    }
}
