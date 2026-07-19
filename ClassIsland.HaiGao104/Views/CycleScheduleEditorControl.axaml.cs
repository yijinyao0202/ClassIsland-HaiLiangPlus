using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Helpers.UI;
using ClassIsland.HaiGao104.Models;
using ClassIsland.HaiGao104.Services;
using ClassIsland.Shared;
using ClassIsland.Shared.Models.Profile;

namespace ClassIsland.HaiGao104.Views;

public sealed partial class CycleScheduleEditorControl : UserControl, INotifyPropertyChanged
{
    private readonly CycleSettingsService _settings;
    private readonly CyclePlanService _cyclePlanService;
    private readonly IScheduleControlBridge _scheduleBridge;
    private readonly IExactTimeService _exactTimeService;
    private readonly List<ClassInfo> _observedClassInfos = [];
    private int _lastSignature;
    private string _summaryText = "正在准备循环课表……";
    private bool _isEmpty = true;
    private bool _canAddDay = true;
    private bool _canRestoreArchive;
    private bool _isRefreshingTimeLayoutSelection;
    private CycleDayHeader? _selectedTodayDay;
    private ArchivedCyclePlan? _selectedArchivedPlan;
    private CycleTimeLayoutOption? _selectedTimeLayout;
    private IReadOnlyList<CycleTimeLayoutOption> _timeLayoutOptions = [];

    public CycleScheduleEditorControl() : this(
        IAppHost.GetService<CycleSettingsService>(),
        IAppHost.GetService<CyclePlanService>(),
        IAppHost.GetService<IScheduleControlBridge>(),
        IAppHost.GetService<IExactTimeService>())
    {
    }

    public CycleScheduleEditorControl(
        CycleSettingsService settings,
        CyclePlanService cyclePlanService,
        IScheduleControlBridge scheduleBridge,
        IExactTimeService exactTimeService)
    {
        _settings = settings;
        _cyclePlanService = cyclePlanService;
        _scheduleBridge = scheduleBridge;
        _exactTimeService = exactTimeService;
        InitializeComponent();
        DataContext = this;
        Refresh(true);
    }

    public new event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<CycleDayHeader> DayHeaders { get; } = [];

    public ObservableCollection<ArchivedCyclePlan> ArchivedPlans { get; } = [];

    public IReadOnlyList<CycleTimeLayoutOption> TimeLayoutOptions
    {
        get => _timeLayoutOptions;
        private set => SetField(ref _timeLayoutOptions, value);
    }

    public ObservableCollection<CycleScheduleRow> Rows { get; } = [];

    public string SummaryText
    {
        get => _summaryText;
        private set => SetField(ref _summaryText, value);
    }

    public bool IsEmpty
    {
        get => _isEmpty;
        private set => SetField(ref _isEmpty, value);
    }

    public bool CanAddDay
    {
        get => _canAddDay;
        private set => SetField(ref _canAddDay, value);
    }

    public bool CanRestoreArchive
    {
        get => _canRestoreArchive;
        private set => SetField(ref _canRestoreArchive, value);
    }

    public CycleDayHeader? SelectedTodayDay
    {
        get => _selectedTodayDay;
        set => SetField(ref _selectedTodayDay, value);
    }

    public ArchivedCyclePlan? SelectedArchivedPlan
    {
        get => _selectedArchivedPlan;
        set
        {
            if (!SetField(ref _selectedArchivedPlan, value))
            {
                return;
            }
            CanRestoreArchive = value is not null && CanAddDay;
        }
    }

    public CycleTimeLayoutOption? SelectedTimeLayout
    {
        get => _selectedTimeLayout;
        set => SetField(ref _selectedTimeLayout, value);
    }

    public void RefreshIfChanged() => Refresh(false);

    public void ReleaseProfileSubscriptions() => UnsubscribeClassInfos();

    private void Refresh_OnClick(object? sender, RoutedEventArgs e) => Refresh(true);

    private void TimeLayout_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingTimeLayoutSelection ||
            sender is not ComboBox { SelectedItem: CycleTimeLayoutOption selectedTimeLayout } ||
            selectedTimeLayout.Id == _settings.ManagedTimeLayoutId)
        {
            return;
        }

        if (_cyclePlanService.SelectManagedTimeLayout(selectedTimeLayout.Id))
        {
            Dispatcher.UIThread.Post(() => Refresh(true), DispatcherPriority.Background);
        }
    }

    private void SetToday_OnClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedTodayDay is not { } selectedDay)
        {
            return;
        }
        _settings.SetTodayWorkDay(selectedDay.DayNumber, _exactTimeService.GetCurrentLocalDateTime());
        Refresh(true);
    }

    private void AddEnd_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_cyclePlanService.InsertDay(DayHeaders.Count))
        {
            Refresh(true);
        }
    }

    private void InsertBefore_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: CycleDayHeader day } &&
            _cyclePlanService.InsertDay(day.Index))
        {
            Refresh(true);
        }
    }

    private void InsertAfter_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: CycleDayHeader day } &&
            _cyclePlanService.InsertDay(day.Index + 1))
        {
            Refresh(true);
        }
    }

    private async void Archive_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: CycleDayHeader day } || !day.CanArchive)
        {
            return;
        }

        var confirmed = await ContentDialogHelper.ShowConfirmationDialog(
            "归档周期课表",
            $"归档 {day.Name} 后，其完整课程内容仍会保留，可随时从顶部恢复到记录的原位置。",
            root: TopLevel.GetTopLevel(this),
            positiveText: "归档");
        if (confirmed && _cyclePlanService.ArchiveDay(day.Index))
        {
            Refresh(true);
        }
    }

    private void RestoreArchived_OnClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedArchivedPlan is not { } archive)
        {
            return;
        }
        if (_cyclePlanService.RestoreArchived(archive.ClassPlanId))
        {
            Refresh(true);
        }
    }

    private void Refresh(bool force)
    {
        var activePlanIds = _cyclePlanService.EnsureManagedSchedules().ToArray();
        var signature = BuildSignature(activePlanIds);
        if (!force && signature == _lastSignature)
        {
            return;
        }
        _lastSignature = signature;

        var selectedTodayPlanId = SelectedTodayDay?.PlanId;
        var selectedArchiveId = SelectedArchivedPlan?.ClassPlanId;
        UnsubscribeClassInfos();
        DayHeaders.Clear();
        ArchivedPlans.Clear();
        Rows.Clear();

        var profile = _scheduleBridge.Profile;
        var now = _exactTimeService.GetCurrentLocalDateTime();
        RefreshTimeLayoutOptions(profile);
        int? currentWorkDayIndex = null;
        if (TemporaryClassPlanResolver.TryGetValid(profile, now, out var temporaryClassPlanId))
        {
            var temporaryIndex = Array.IndexOf(activePlanIds, temporaryClassPlanId);
            if (temporaryIndex >= 0)
            {
                currentWorkDayIndex = temporaryIndex;
            }
        }
        else if (_settings.TryGetEffectiveWorkDayIndex(now, out var effectiveIndex))
        {
            currentWorkDayIndex = effectiveIndex;
        }

        for (var index = 0; index < activePlanIds.Length; index++)
        {
            var dayNumber = index + 1;
            DayHeaders.Add(new CycleDayHeader(
                activePlanIds[index],
                index,
                dayNumber,
                CycleDayNameFormatter.GetName(dayNumber),
                currentWorkDayIndex == index ? "今天" : $"第 {dayNumber} 天",
                currentWorkDayIndex == index,
                activePlanIds.Length > 1));
        }

        foreach (var archive in _settings.ArchivedCyclePlans.OrderBy(item => item.ArchivedAt))
        {
            ArchivedPlans.Add(archive);
        }

        CanAddDay = activePlanIds.Length < 100;
        SelectedTodayDay = DayHeaders.FirstOrDefault(item => item.PlanId == selectedTodayPlanId)
                           ?? DayHeaders.FirstOrDefault(item => item.IsCurrent)
                           ?? DayHeaders.FirstOrDefault();
        SelectedArchivedPlan = ArchivedPlans.FirstOrDefault(item => item.ClassPlanId == selectedArchiveId)
                               ?? ArchivedPlans.FirstOrDefault();
        CanRestoreArchive = SelectedArchivedPlan is not null && CanAddDay;

        SummaryText = activePlanIds.Length == 0
            ? "正在创建周期课表，请稍候刷新。"
            : $"共 {activePlanIds.Length} 个上课日：可在任意周前后插入、归档并原位恢复；周期最多 100 天。";

        if (_settings.ManagedTimeLayoutId is not { } timeLayoutId ||
            !profile.TimeLayouts.TryGetValue(timeLayoutId, out var timeLayout))
        {
            IsEmpty = true;
            return;
        }

        var lessonTimePoints = timeLayout.Layouts.Where(item => item.TimeType == 0).ToArray();
        var plans = activePlanIds
            .Where(profile.ClassPlans.ContainsKey)
            .Select(id => profile.ClassPlans[id])
            .ToArray();
        var profileChanged = false;
        foreach (var plan in plans)
        {
            while (plan.Classes.Count < lessonTimePoints.Length)
            {
                plan.Classes.Add(new ClassInfo());
                profileChanged = true;
            }
        }

        var subjectOptions = new List<CycleSubjectOption>
        {
            new(Guid.Empty, "（未设置）")
        };
        subjectOptions.AddRange(profile.Subjects
            .OrderBy(item => item.Value.Name)
            .Select(item => new CycleSubjectOption(item.Key, item.Value.Name)));

        for (var rowIndex = 0; rowIndex < lessonTimePoints.Length; rowIndex++)
        {
            var timePoint = lessonTimePoints[rowIndex];
            var cells = plans
                .Select(plan => new CycleScheduleCell(plan.Classes[rowIndex], subjectOptions))
                .ToArray();
            Rows.Add(new CycleScheduleRow(
                $"第 {rowIndex + 1} 节",
                $"{timePoint.StartTime:hh\\:mm}-{timePoint.EndTime:hh\\:mm}",
                cells));
            foreach (var cell in cells)
            {
                ObserveClassInfo(cell.ClassInfo);
            }
        }

        if (profileChanged)
        {
            _scheduleBridge.SaveProfile();
        }
        IsEmpty = lessonTimePoints.Length == 0;
    }

    private int BuildSignature(IReadOnlyList<Guid> activePlanIds)
    {
        var hash = new HashCode();
        var profile = _scheduleBridge.Profile;
        hash.Add(_settings.WorkDays);
        var now = _exactTimeService.GetCurrentLocalDateTime();
        var effectiveTimeLayoutId = _settings.GetEffectiveTimeLayoutId(now);
        hash.Add(effectiveTimeLayoutId);
        hash.Add(_settings.ManagedTimeLayoutId);
        hash.Add(_settings.RestRepeatStartDate);
        hash.Add(_settings.RestRepeatEndDate);
        hash.Add(_settings.RestRepeatStartDay);
        hash.Add(profile.TempClassPlanId);
        hash.Add(profile.TempClassPlanSetupTime.Date);
        foreach (var id in activePlanIds)
        {
            hash.Add(id);
            if (profile.ClassPlans.TryGetValue(id, out var plan))
            {
                hash.Add(plan.Classes.Count);
            }
        }
        foreach (var archive in _settings.ArchivedCyclePlans)
        {
            hash.Add(archive);
        }
        foreach (var (timeLayoutId, timeLayout) in profile.TimeLayouts)
        {
            hash.Add(timeLayoutId);
            hash.Add(timeLayout.Name);
            foreach (var item in timeLayout.Layouts)
            {
                hash.Add(item.TimeType);
                hash.Add(item.StartTime);
                hash.Add(item.EndTime);
            }
        }
        foreach (var subject in profile.Subjects)
        {
            hash.Add(subject.Key);
            hash.Add(subject.Value.Name);
        }
        hash.Add(_settings.GetCyclePosition(now));
        return hash.ToHashCode();
    }

    private void RefreshTimeLayoutOptions(Profile profile)
    {
        _isRefreshingTimeLayoutSelection = true;
        try
        {
            var selectedTimeLayoutId = _settings.ManagedTimeLayoutId;
            var options = profile.TimeLayouts
                .OrderBy(item => item.Value.Name)
                .Select(item => new CycleTimeLayoutOption(
                    item.Key,
                    string.IsNullOrWhiteSpace(item.Value.Name) ? "（未命名时间表）" : item.Value.Name,
                    item.Value.Layouts.Count(layout => layout.TimeType == 0)))
                .ToArray();
            if (!TimeLayoutOptions.SequenceEqual(options))
            {
                TimeLayoutOptions = options;
            }

            SelectedTimeLayout = TimeLayoutOptions.FirstOrDefault(item => item.Id == selectedTimeLayoutId)
                                 ?? TimeLayoutOptions.FirstOrDefault();
        }
        finally
        {
            _isRefreshingTimeLayoutSelection = false;
        }
    }

    private void ObserveClassInfo(ClassInfo classInfo)
    {
        if (_observedClassInfos.Contains(classInfo))
        {
            return;
        }
        _observedClassInfos.Add(classInfo);
        classInfo.PropertyChanged += ClassInfo_OnPropertyChanged;
    }

    private void UnsubscribeClassInfos()
    {
        foreach (var classInfo in _observedClassInfos)
        {
            classInfo.PropertyChanged -= ClassInfo_OnPropertyChanged;
        }
        _observedClassInfos.Clear();
    }

    private void ClassInfo_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClassInfo.SubjectId))
        {
            _scheduleBridge.SaveProfile();
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed record CycleDayHeader(
    Guid PlanId,
    int Index,
    int DayNumber,
    string Name,
    string Description,
    bool IsCurrent,
    bool CanArchive);

public sealed record CycleSubjectOption(Guid Id, string Name);

public sealed record CycleTimeLayoutOption(Guid Id, string Name, int LessonCount)
{
    public string Description => $"{LessonCount} 个上课时间点";
}

public sealed record CycleScheduleCell(ClassInfo ClassInfo, IReadOnlyList<CycleSubjectOption> SubjectOptions);

public sealed record CycleScheduleRow(string LessonName, string TimeText, IReadOnlyList<CycleScheduleCell> Cells);
