using Avalonia.Controls;
using Avalonia.Threading;
using ClassIsland.Core;
using FluentAvalonia.UI.Controls;

namespace ClassIsland.HaiGao104.Services;

internal static class PluginDialogHost
{
    private const double MinimumOwnerWidth = 520;
    private const double MinimumOwnerHeight = 360;

    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        var owner = FindSuitableOwner();
        if (owner is not null)
        {
            return await dialog.ShowAsync(owner);
        }

        var host = new Window
        {
            Title = dialog.Title?.ToString() ?? "HL Education +",
            Width = 760,
            Height = 700,
            MinWidth = 560,
            MinHeight = 480,
            CanResize = true,
            ShowActivated = true,
            ShowInTaskbar = true,
            SystemDecorations = SystemDecorations.None,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new Grid()
        };

        host.Show();
        host.Activate();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        try
        {
            return await dialog.ShowAsync(host);
        }
        finally
        {
            host.Close();
        }
    }

    private static Window? FindSuitableOwner() => AppBase.Current.DesktopLifetime?.Windows
        .Where(window =>
            window.IsVisible &&
            window.PlatformImpl is not null &&
            window.Bounds.Width >= MinimumOwnerWidth &&
            window.Bounds.Height >= MinimumOwnerHeight)
        .OrderByDescending(window => window.IsActive)
        .FirstOrDefault();
}
