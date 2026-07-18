using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.HaiGao104.Views;
using Microsoft.Extensions.Hosting;

namespace ClassIsland.HaiGao104.Services;

public sealed class CycleScheduleEditorHostService(
    CycleSettingsService settings,
    CyclePlanService cyclePlanService,
    IScheduleControlBridge scheduleBridge,
    IExactTimeService exactTimeService) : IHostedService
{
    private readonly Dictionary<Control, CycleScheduleEditorControl> _editors = [];
    private readonly Dictionary<Window, RestDayOverrideControl> _restDayControls = [];
    private DispatcherTimer? _timer;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Dispatcher.UIThread.Post(StartOnUiThread);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispatcher.UIThread.Post(StopOnUiThread);
        return Task.CompletedTask;
    }

    private void StartOnUiThread()
    {
        if (_timer is not null)
        {
            return;
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _timer.Tick += Timer_OnTick;
        _timer.Start();
        ScanWindows();
    }

    private void StopOnUiThread()
    {
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= Timer_OnTick;
            _timer = null;
        }
        RestoreInjectedControls();
    }

    private void Timer_OnTick(object? sender, EventArgs e) => ScanWindows();

    private void ScanWindows()
    {
        CleanupClosedEditors();
        if (!settings.IsEnabled)
        {
            RestoreInjectedControls();
            return;
        }

        foreach (var window in AppBase.Current.DesktopLifetime?.Windows ?? [])
        {
            if (window.GetType().FullName != "ClassIsland.Views.ProfileSettingsWindow")
            {
                continue;
            }

            EnsureRestDayControl(window);

            var originalEditor = window.GetVisualDescendants()
                .OfType<Control>()
                .FirstOrDefault(control =>
                    control.Name == "ScheduleDataGrid" &&
                    control.GetType().FullName == "ClassIsland.Controls.ScheduleDataGrid.ScheduleDataGrid");
            if (originalEditor is null)
            {
                continue;
            }

            if (_editors.TryGetValue(originalEditor, out var existingEditor))
            {
                existingEditor.RefreshIfChanged();
                continue;
            }

            if (originalEditor.Parent is not Grid parent)
            {
                continue;
            }

            var editor = new CycleScheduleEditorControl(settings, cyclePlanService, scheduleBridge, exactTimeService);
            Grid.SetRow(editor, Grid.GetRow(originalEditor));
            Grid.SetColumn(editor, Grid.GetColumn(originalEditor));
            Grid.SetRowSpan(editor, Grid.GetRowSpan(originalEditor));
            Grid.SetColumnSpan(editor, Grid.GetColumnSpan(originalEditor));
            parent.Children.Add(editor);
            originalEditor.IsVisible = false;
            _editors.Add(originalEditor, editor);
        }
    }

    private void CleanupClosedEditors()
    {
        foreach (var originalEditor in _editors.Keys
                     .Where(control => !control.IsAttachedToVisualTree())
                     .ToArray())
        {
            RemoveEditor(originalEditor);
        }

        var openWindows = AppBase.Current.DesktopLifetime?.Windows ?? [];
        foreach (var window in _restDayControls.Keys.Where(window => !openWindows.Contains(window)).ToArray())
        {
            RemoveRestDayControl(window);
        }
    }

    private void RestoreInjectedControls()
    {
        foreach (var originalEditor in _editors.Keys.ToArray())
        {
            RemoveEditor(originalEditor);
        }
        foreach (var window in _restDayControls.Keys.ToArray())
        {
            RemoveRestDayControl(window);
        }
    }

    private void RemoveEditor(Control originalEditor)
    {
        if (!_editors.Remove(originalEditor, out var editor))
        {
            return;
        }
        if (editor.Parent is Panel parent)
        {
            parent.Children.Remove(editor);
        }
        originalEditor.IsVisible = true;
    }

    private void EnsureRestDayControl(Window window)
    {
        if (_restDayControls.TryGetValue(window, out var existingControl))
        {
            existingControl.RefreshIfChanged();
            return;
        }

        if (window.FindResource("TemporaryClassPlan") is not Grid outerGrid ||
            outerGrid.Children.OfType<Grid>().FirstOrDefault() is not { } contentGrid ||
            contentGrid.Children.OfType<StackPanel>().FirstOrDefault(item => Grid.GetRow(item) == 0) is not { } targetPanel)
        {
            return;
        }

        var control = targetPanel.Children.OfType<RestDayOverrideControl>().FirstOrDefault()
                      ?? new RestDayOverrideControl(settings, scheduleBridge, exactTimeService);
        if (control.Parent is null)
        {
            targetPanel.Children.Add(control);
        }
        _restDayControls.Add(window, control);
    }

    private void RemoveRestDayControl(Window window)
    {
        if (!_restDayControls.Remove(window, out var control))
        {
            return;
        }
        if (control.Parent is Panel parent)
        {
            parent.Children.Remove(control);
        }
    }
}
