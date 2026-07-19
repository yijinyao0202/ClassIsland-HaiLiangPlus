using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.HaiGao104.Views;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassIsland.HaiGao104.Services;

public sealed class CycleScheduleEditorHostService(
    CycleSettingsService settings,
    CyclePlanService cyclePlanService,
    IScheduleControlBridge scheduleBridge,
    IExactTimeService exactTimeService,
    ILogger<CycleScheduleEditorHostService> logger) : IHostedService
{
    private readonly Dictionary<Control, CycleScheduleEditorControl> _editors = [];
    private readonly HashSet<Control> _pendingEditors = [];
    private readonly Dictionary<Window, RestDayOverrideControl> _restDayControls = [];
    private IDisposable? _editorLoadedSubscription;
    private Style? _originalEditorSuppressionStyle;
    private DispatcherTimer? _timer;
    private int _scanQueued;

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

        settings.SettingsChanged += Settings_OnChanged;
        UpdateOriginalEditorSuppressionStyle();
        _editorLoadedSubscription = Control.LoadedEvent.AddClassHandler<Control>(
            OriginalEditor_OnLoaded,
            handledEventsToo: true);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _timer.Tick += Timer_OnTick;
        _timer.Start();
        QueueScan();
    }

    private void StopOnUiThread()
    {
        settings.SettingsChanged -= Settings_OnChanged;
        _editorLoadedSubscription?.Dispose();
        _editorLoadedSubscription = null;
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= Timer_OnTick;
            _timer = null;
        }
        Interlocked.Exchange(ref _scanQueued, 0);
        _pendingEditors.Clear();
        RemoveOriginalEditorSuppressionStyle();
        RestoreInjectedControls();
    }

    private void Timer_OnTick(object? sender, EventArgs e) => QueueScan();

    private void Settings_OnChanged(object? sender, EventArgs e) => QueueScan();

    private void OriginalEditor_OnLoaded(Control control, RoutedEventArgs e)
    {
        if (!settings.HasCompletedOnboarding || !IsOriginalEditor(control))
        {
            return;
        }

        QueueScan();
    }

    private void QueueScan()
    {
        if (Interlocked.Exchange(ref _scanQueued, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _scanQueued, 0);
            if (_timer is null)
            {
                return;
            }

            try
            {
                ScanWindows();
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "刷新 HL Education + 课表编辑器失败。");
            }
        }, DispatcherPriority.Background);
    }

    private void ScanWindows()
    {
        UpdateOriginalEditorSuppressionStyle();
        CleanupClosedEditors();
        if (!settings.HasCompletedOnboarding)
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

            if (settings.IsTakeoverEnabled)
            {
                EnsureRestDayControl(window);
            }
            else
            {
                RemoveRestDayControl(window);
            }

            var originalEditor = window.GetVisualDescendants()
                .OfType<Control>()
                .FirstOrDefault(IsOriginalEditor);
            if (originalEditor is null)
            {
                continue;
            }

            EnsureEditor(originalEditor);
        }
    }

    private void EnsureEditor(Control originalEditor)
    {
        if (_editors.TryGetValue(originalEditor, out var existingEditor))
        {
            existingEditor.RefreshIfChanged();
            return;
        }

        if (!_pendingEditors.Add(originalEditor))
        {
            return;
        }

        try
        {
            if (originalEditor.Parent is not Grid parent)
            {
                return;
            }

            var editor = new CycleScheduleEditorControl(settings, cyclePlanService, scheduleBridge, exactTimeService);
            Grid.SetRow(editor, Grid.GetRow(originalEditor));
            Grid.SetColumn(editor, Grid.GetColumn(originalEditor));
            Grid.SetRowSpan(editor, Grid.GetRowSpan(originalEditor));
            Grid.SetColumnSpan(editor, Grid.GetColumnSpan(originalEditor));
            _editors.Add(originalEditor, editor);
            originalEditor.IsVisible = false;
            try
            {
                parent.Children.Add(editor);
            }
            catch
            {
                originalEditor.IsVisible = true;
                _editors.Remove(originalEditor);
                editor.ReleaseProfileSubscriptions();
                throw;
            }
        }
        finally
        {
            _pendingEditors.Remove(originalEditor);
        }
    }

    private void UpdateOriginalEditorSuppressionStyle()
    {
        if (!settings.HasCompletedOnboarding)
        {
            RemoveOriginalEditorSuppressionStyle();
            return;
        }

        if (_originalEditorSuppressionStyle is not null)
        {
            return;
        }

        var originalEditorType = Type.GetType(
            "ClassIsland.Controls.ScheduleDataGrid.ScheduleDataGrid, ClassIsland",
            throwOnError: false);
        if (originalEditorType is null)
        {
            return;
        }

        _originalEditorSuppressionStyle = new Style
        {
            Selector = Selectors.Name(
                Selectors.Is(null, originalEditorType),
                "ScheduleDataGrid"),
            Setters =
            {
                new Setter(Visual.IsVisibleProperty, false)
            }
        };
        AppBase.Current.Styles.Add(_originalEditorSuppressionStyle);
    }

    private void RemoveOriginalEditorSuppressionStyle()
    {
        if (_originalEditorSuppressionStyle is null)
        {
            return;
        }

        AppBase.Current.Styles.Remove(_originalEditorSuppressionStyle);
        _originalEditorSuppressionStyle = null;
    }

    private static bool IsOriginalEditor(Control control) =>
        control.Name == "ScheduleDataGrid" &&
        control.GetType().FullName == "ClassIsland.Controls.ScheduleDataGrid.ScheduleDataGrid";

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
        editor.ReleaseProfileSubscriptions();
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
