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

namespace ClassIsland.HaiGao104.Services;

public sealed class CycleScheduleEditorHostService(
    CycleSettingsService settings,
    CyclePlanService cyclePlanService,
    IScheduleControlBridge scheduleBridge,
    IExactTimeService exactTimeService) : IHostedService
{
    private readonly Dictionary<Control, CycleScheduleEditorControl> _editors = [];
    private readonly Dictionary<Window, RestDayOverrideControl> _restDayControls = [];
    private IDisposable? _editorLoadedSubscription;
    private Style? _originalEditorSuppressionStyle;
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

        settings.SettingsChanged += Settings_OnChanged;
        UpdateOriginalEditorSuppressionStyle();
        _editorLoadedSubscription = Control.LoadedEvent.AddClassHandler<Control>(
            OriginalEditor_OnLoaded,
            handledEventsToo: true);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _timer.Tick += Timer_OnTick;
        _timer.Start();
        ScanWindows();
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
        RemoveOriginalEditorSuppressionStyle();
        RestoreInjectedControls();
    }

    private void Timer_OnTick(object? sender, EventArgs e) => ScanWindows();

    private void Settings_OnChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(ScanWindows, DispatcherPriority.Send);

    private void OriginalEditor_OnLoaded(Control control, RoutedEventArgs e)
    {
        if (!settings.HasCompletedOnboarding || !IsOriginalEditor(control))
        {
            return;
        }

        EnsureEditor(control);
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

        if (originalEditor.Parent is not Grid parent)
        {
            return;
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
