using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace ClassIsland.HaiGaoAutoShutdown.Views;

public partial class ShutdownCountdownWindow : Avalonia.Controls.Window
{
    public ShutdownCountdownWindow() : this(false)
    {
    }

    public ShutdownCountdownWindow(bool isPreview)
    {
        InitializeComponent();
        PreviewBadge.IsVisible = isPreview;
        AddHandler(
            InputElement.PointerPressedEvent,
            OnPointerPressed,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        KeyDown += OnKeyDown;
    }

    public event EventHandler? CancelRequested;

    public void SetCountdown(int remainingSeconds)
    {
        MessageText.Text = $"将在 {remainingSeconds} 秒关机，点击屏幕任意处取消";
        MessageText.Foreground = Brushes.White;
        HintText.Text = "点击屏幕任意处，或按 Esc，取消本次关机";
    }

    public void SetStatus(string message)
    {
        MessageText.Text = message;
        MessageText.Foreground = Brushes.White;
        HintText.Text = string.Empty;
    }

    public void SetError(string message)
    {
        MessageText.Text = message;
        MessageText.Foreground = Brushes.OrangeRed;
        HintText.Text = "点击屏幕任意处关闭提示";
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs args) =>
        CancelRequested?.Invoke(this, EventArgs.Empty);

    private void OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape)
        {
            return;
        }
        args.Handled = true;
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }
}
