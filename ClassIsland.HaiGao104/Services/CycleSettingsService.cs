using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassIsland.HaiGao104.Abstractions;
using ClassIsland.HaiGao104.Models;

namespace ClassIsland.HaiGao104.Services;

public sealed class CycleSettingsService : INotifyPropertyChanged, ICycleCalendar
{
    private const string DefaultRotationName = "班级轮换";
    private const int CurrentOnboardingVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsPath;
    private bool _isLoading;
    private bool _isEnabled;
    private int _onboardingVersion;
    private DateTimeOffset _anchorDate = new(DateTime.Today);
    private int _workDays = 10;
    private int _restDays = 4;
    private int _currentCycleDay = 1;
    private Guid? _fixedClassPlanId;
    private Guid? _managedClassPlanId;
    private List<Guid> _managedClassPlanIds = [];
    private List<ArchivedCyclePlan> _archivedCyclePlans = [];
    private Guid? _managedTimeLayoutId;
    private DateTime? _restRepeatStartDate;
    private DateTime? _restRepeatEndDate;
    private int _restRepeatStartDay = 1;
    private DateTime? _ignoredRestPromptDate;
    private DateTime? _temporaryClassPlanOverrideDate;
    private bool _isRotationEnabled = true;
    private string _rotationName = DefaultRotationName;
    private bool _useCalendarDateNumber = true;
    private DateTimeOffset _rotationAnchorDate = new(DateTime.Today);
    private int _currentRotationDay = 1;
    private bool _isRotationSpeechEnabled = true;
    private int _batchTimeLayoutMigrationVersion;
    private bool _wasDisabledByPlugin;
    private string _todayStatus = "正在计算今日作息…";
    private string _nextSwitchText = "";
    private string _rotationStatus = "正在检查班级批次时间表…";

    public CycleSettingsService(string settingsPath)
    {
        _settingsPath = settingsPath;
        RotationSteps.CollectionChanged += OnRotationStepsChanged;
        Load();
        RefreshStatus(DateTime.Now);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? SettingsChanged;

    public ObservableCollection<RotationStep> RotationSteps { get; } = [];

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (value && !HasCompletedOnboarding)
            {
                return;
            }

            if (SetSetting(ref _isEnabled, value))
            {
                OnPropertyChanged(nameof(IsTakeoverEnabled));
                OnPropertyChanged(nameof(IsCycleActive));
            }
        }
    }

    public bool HasCompletedOnboarding => _onboardingVersion >= CurrentOnboardingVersion;

    public bool IsTakeoverEnabled => IsEnabled && HasCompletedOnboarding;

    public bool IsCycleActive => IsTakeoverEnabled;

    public DateTimeOffset AnchorDate
    {
        get => _anchorDate;
        set => SetAnchorDate(value.Date);
    }

    public int WorkDays
    {
        get => _workDays;
        set
        {
            var normalized = Math.Clamp(value, 1, 100);
            if (_workDays == normalized)
            {
                return;
            }

            _workDays = normalized;
            ClearRestRepeatCore();
            _ignoredRestPromptDate = null;
            OnPropertyChanged(nameof(WorkDays));
            OnPropertyChanged(nameof(CycleLength));
            OnPropertyChanged(nameof(ManagedPlanRangeText));
            OnPropertyChanged(nameof(IgnoredRestPromptDate));
            SaveAndNotify(DateTime.Now);
        }
    }

    public int RestDays
    {
        get => _restDays;
        set
        {
            var normalized = Math.Clamp(value, 1, 100);
            if (_restDays == normalized)
            {
                return;
            }

            _restDays = normalized;
            ClearRestRepeatCore();
            _ignoredRestPromptDate = null;
            OnPropertyChanged(nameof(RestDays));
            OnPropertyChanged(nameof(CycleLength));
            OnPropertyChanged(nameof(IgnoredRestPromptDate));
            SaveAndNotify(DateTime.Now);
        }
    }

    public int CycleLength => WorkDays + RestDays;

    public string ManagedPlanRangeText =>
        $"原课表编辑器显示 {CycleDayNameFormatter.GetName(1)} 至 {CycleDayNameFormatter.GetName(WorkDays)}，每个上课日可单独编辑。";

    public int CurrentCycleDay
    {
        get => _currentCycleDay;
        set => SetCurrentCycleDay(value, DateTime.Today, true);
    }

    public Guid? FixedClassPlanId
    {
        get => _fixedClassPlanId;
        set => SetSetting(ref _fixedClassPlanId, value);
    }

    public Guid? ManagedClassPlanId => _managedClassPlanId;

    public IReadOnlyList<Guid> ManagedClassPlanIds => _managedClassPlanIds;

    public IReadOnlyList<ArchivedCyclePlan> ArchivedCyclePlans => _archivedCyclePlans;

    public Guid? ManagedTimeLayoutId => _managedTimeLayoutId;

    public DateTime? RestRepeatStartDate => _restRepeatStartDate;

    public DateTime? RestRepeatEndDate => _restRepeatEndDate;

    public int RestRepeatStartDay => _restRepeatStartDay;

    public DateTime? IgnoredRestPromptDate => _ignoredRestPromptDate;

    public bool IsRotationEnabled
    {
        get => _isRotationEnabled;
        set => SetSetting(ref _isRotationEnabled, value);
    }

    public string RotationName
    {
        get => _rotationName;
        set => SetSetting(ref _rotationName, string.IsNullOrWhiteSpace(value) ? DefaultRotationName : value.Trim());
    }

    public bool UseCalendarDateNumber
    {
        get => _useCalendarDateNumber;
        set
        {
            if (SetSetting(ref _useCalendarDateNumber, value))
            {
                OnPropertyChanged(nameof(IsContinuousRotation));
            }
        }
    }

    public bool IsContinuousRotation
    {
        get => !UseCalendarDateNumber;
        set
        {
            if (value)
            {
                UseCalendarDateNumber = false;
            }
        }
    }

    public int CurrentRotationDay
    {
        get => _currentRotationDay;
        set => SetCurrentRotationDay(value);
    }

    public int RotationDayMaximum => Math.Max(1, RotationSteps.Count);

    public bool IsRotationSpeechEnabled
    {
        get => _isRotationSpeechEnabled;
        set => SetSetting(ref _isRotationSpeechEnabled, value);
    }

    public string TodayStatus
    {
        get => _todayStatus;
        private set => SetField(ref _todayStatus, value);
    }

    public string NextSwitchText
    {
        get => _nextSwitchText;
        private set => SetField(ref _nextSwitchText, value);
    }

    public string RotationStatus
    {
        get => _rotationStatus;
        private set => SetField(ref _rotationStatus, value);
    }

    internal bool WasDisabledByPlugin => _wasDisabledByPlugin;

    public bool IsRestDay(DateTime date) =>
        IsTakeoverEnabled &&
        !HasTemporaryClassPlanOverride(date) &&
        !HasActiveRestRepeat(date) &&
        IsNominalRestDay(date);

    public void CompleteOnboarding(bool enableTakeover)
    {
        _onboardingVersion = CurrentOnboardingVersion;
        _isEnabled = enableTakeover;
        OnPropertyChanged(nameof(HasCompletedOnboarding));
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(IsTakeoverEnabled));
        OnPropertyChanged(nameof(IsCycleActive));
        SaveAndNotify(DateTime.Now);
    }

    internal bool IsNominalRestDay(DateTime date) => GetCyclePosition(date) >= WorkDays;

    internal bool HasActiveRestRepeat(DateTime date) =>
        _restRepeatStartDate is { } start &&
        _restRepeatEndDate is { } end &&
        date.Date >= start.Date &&
        date.Date <= end.Date;

    internal bool HasTemporaryClassPlanOverride(DateTime date) =>
        _temporaryClassPlanOverrideDate?.Date == date.Date;

    internal bool TryGetEffectiveWorkDayIndex(DateTime date, out int workDayIndex)
    {
        if (HasActiveRestRepeat(date))
        {
            var elapsed = (date.Date - _restRepeatStartDate!.Value.Date).Days;
            workDayIndex = PositiveModulo(_restRepeatStartDay - 1 + elapsed, WorkDays);
            return true;
        }

        var position = GetCyclePosition(date);
        if (position < WorkDays)
        {
            workDayIndex = position;
            return true;
        }

        workDayIndex = -1;
        return false;
    }

    internal RotationStep? GetRotationStep(DateTime date)
    {
        var index = GetRotationIndex(date);
        return index < 0 ? null : RotationSteps[index];
    }

    internal Guid? GetEffectiveTimeLayoutId(DateTime date)
    {
        if (IsRotationEnabled && GetRotationStep(date)?.TimeLayoutId is { } rotationTimeLayoutId)
        {
            return rotationTimeLayoutId;
        }

        return ManagedTimeLayoutId;
    }

    internal void SetManagedScheduleState(
        IReadOnlyList<Guid> classPlanIds,
        Guid timeLayoutId,
        IReadOnlyList<ArchivedCyclePlan>? archivedCyclePlans = null)
    {
        var normalizedClassPlanIds = classPlanIds.Where(id => id != Guid.Empty).Distinct().ToList();
        var normalizedArchivedPlans = (archivedCyclePlans ?? _archivedCyclePlans)
            .Where(item => item.ClassPlanId != Guid.Empty && !normalizedClassPlanIds.Contains(item.ClassPlanId))
            .DistinctBy(item => item.ClassPlanId)
            .ToList();
        var firstClassPlanId = normalizedClassPlanIds.FirstOrDefault();
        var classPlanChanged = !_managedClassPlanIds.SequenceEqual(normalizedClassPlanIds) ||
                               _managedClassPlanId != firstClassPlanId ||
                               _fixedClassPlanId != firstClassPlanId;
        var archiveChanged = !_archivedCyclePlans.SequenceEqual(normalizedArchivedPlans);
        var timeLayoutChanged = _managedTimeLayoutId != timeLayoutId;
        if (!classPlanChanged && !archiveChanged && !timeLayoutChanged)
        {
            return;
        }

        _managedClassPlanIds = normalizedClassPlanIds;
        _archivedCyclePlans = normalizedArchivedPlans;
        _managedClassPlanId = firstClassPlanId == Guid.Empty ? null : firstClassPlanId;
        _managedTimeLayoutId = timeLayoutId;
        _fixedClassPlanId = _managedClassPlanId;
        if (classPlanChanged)
        {
            OnPropertyChanged(nameof(ManagedClassPlanIds));
            OnPropertyChanged(nameof(ManagedClassPlanId));
            OnPropertyChanged(nameof(FixedClassPlanId));
        }
        if (archiveChanged)
        {
            OnPropertyChanged(nameof(ArchivedCyclePlans));
        }
        if (timeLayoutChanged)
        {
            OnPropertyChanged(nameof(ManagedTimeLayoutId));
        }

        SaveAndNotify(DateTime.Now);
    }

    internal void ApplyCyclePlanMutation(
        IReadOnlyList<Guid> classPlanIds,
        IReadOnlyList<ArchivedCyclePlan> archivedCyclePlans,
        DateTime today,
        int todayCycleDay,
        int? restRepeatStartDay)
    {
        var normalizedClassPlanIds = classPlanIds.Where(id => id != Guid.Empty).Distinct().Take(100).ToList();
        if (normalizedClassPlanIds.Count == 0)
        {
            throw new InvalidOperationException("周期课表至少需要保留一天。");
        }

        _managedClassPlanIds = normalizedClassPlanIds;
        _archivedCyclePlans = archivedCyclePlans
            .Where(item => !_managedClassPlanIds.Contains(item.ClassPlanId))
            .DistinctBy(item => item.ClassPlanId)
            .ToList();
        _workDays = Math.Clamp(_managedClassPlanIds.Count, 1, 100);
        _managedClassPlanId = _managedClassPlanIds[0];
        _fixedClassPlanId = _managedClassPlanId;
        _currentCycleDay = Math.Clamp(todayCycleDay, 1, CycleLength);
        _anchorDate = new DateTimeOffset(today.Date.AddDays(-(_currentCycleDay - 1)));
        if (restRepeatStartDay is { } repeatStartDay && _restRepeatStartDate is not null)
        {
            _restRepeatStartDay = Math.Clamp(repeatStartDay, 1, WorkDays);
        }

        OnPropertyChanged(nameof(ManagedClassPlanIds));
        OnPropertyChanged(nameof(ArchivedCyclePlans));
        OnPropertyChanged(nameof(ManagedClassPlanId));
        OnPropertyChanged(nameof(FixedClassPlanId));
        OnPropertyChanged(nameof(WorkDays));
        OnPropertyChanged(nameof(CycleLength));
        OnPropertyChanged(nameof(ManagedPlanRangeText));
        OnPropertyChanged(nameof(CurrentCycleDay));
        OnPropertyChanged(nameof(AnchorDate));
        OnPropertyChanged(nameof(RestRepeatStartDay));
        SaveAndNotify(today);
    }

    public void SetTodayWorkDay(int workDay, DateTime today)
    {
        SetCurrentCycleDay(Math.Clamp(workDay, 1, WorkDays), today, true);
    }

    public void StartRestRepeat(DateTime date, int startWorkDay)
    {
        if (!IsNominalRestDay(date))
        {
            return;
        }

        var position = GetCyclePosition(date);
        _restRepeatStartDate = date.Date;
        _restRepeatEndDate = date.Date.AddDays(CycleLength - position - 1);
        _restRepeatStartDay = Math.Clamp(startWorkDay, 1, WorkDays);
        _ignoredRestPromptDate = null;
        OnPropertyChanged(nameof(RestRepeatStartDate));
        OnPropertyChanged(nameof(RestRepeatEndDate));
        OnPropertyChanged(nameof(RestRepeatStartDay));
        OnPropertyChanged(nameof(IgnoredRestPromptDate));
        SaveAndNotify(date);
    }

    public void IgnoreRestDay(DateTime date)
    {
        ClearRestRepeatCore();
        _ignoredRestPromptDate = date.Date;
        OnPropertyChanged(nameof(IgnoredRestPromptDate));
        SaveAndNotify(date);
    }

    public void ClearRestRepeat(DateTime date)
    {
        if (!ClearRestRepeatCore())
        {
            return;
        }
        SaveAndNotify(date);
    }

    internal void SetTemporaryClassPlanOverrideDate(DateTime? date)
    {
        var normalizedDate = date?.Date;
        if (_temporaryClassPlanOverrideDate == normalizedDate)
        {
            return;
        }
        _temporaryClassPlanOverrideDate = normalizedDate;
        RefreshStatus(date ?? DateTime.Now);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void AddRotationStep()
    {
        var nextNumber = RotationSteps.Count + 1;
        var suggestedTime = RotationSteps.Count == 0
            ? RoundUpToFiveMinutes(DateTime.Now.TimeOfDay)
            : NormalizeTime(RotationSteps[RotationSteps.Count - 1].Time.Add(TimeSpan.FromMinutes(5)));
        RotationSteps.Add(new RotationStep(GetDefaultBatchName(nextNumber), suggestedTime));
    }

    public void RemoveRotationStep(RotationStep step) => RotationSteps.Remove(step);

    internal void SetDisabledByPlugin(bool value)
    {
        if (_wasDisabledByPlugin == value)
        {
            return;
        }
        _wasDisabledByPlugin = value;
        Save();
    }

    internal void RefreshStatus(DateTime date)
    {
        var position = GetCyclePosition(date);
        var nominalRestDay = position >= WorkDays;
        var phaseDay = nominalRestDay ? position - WorkDays + 1 : position + 1;
        var phaseLength = nominalRestDay ? RestDays : WorkDays;
        var phaseName = nominalRestDay ? "休息日" : "上课日";
        var daysUntilSwitch = nominalRestDay ? CycleLength - position : WorkDays - position;
        var nextSwitchDate = date.Date.AddDays(daysUntilSwitch);
        var nextPhaseName = nominalRestDay ? "上课" : "休息";

        SetField(ref _currentCycleDay, position + 1, nameof(CurrentCycleDay));
        if (HasTemporaryClassPlanOverride(date))
        {
            TodayStatus = $"今天原定为{phaseName}第 {phaseDay}/{phaseLength} 天，正在使用当天临时课表";
        }
        else if (HasActiveRestRepeat(date) && TryGetEffectiveWorkDayIndex(date, out var repeatedIndex))
        {
            TodayStatus = $"今天原定休息，临时重复 {CycleDayNameFormatter.GetName(repeatedIndex + 1)}";
        }
        else
        {
            TodayStatus = $"今天是第 {position + 1}/{CycleLength} 个周期日：{phaseName}第 {phaseDay}/{phaseLength} 天";
        }
        NextSwitchText = $"{nextSwitchDate:yyyy 年 M 月 d 日}开始{nextPhaseName}";
        RefreshRotationStatus(date);
    }

    internal int GetCyclePosition(DateTime date)
    {
        var elapsedDays = (date.Date - AnchorDate.Date).Days;
        return PositiveModulo(elapsedDays, CycleLength);
    }

    private void SetAnchorDate(DateTime anchorDate)
    {
        var normalized = new DateTimeOffset(anchorDate.Date);
        if (_anchorDate == normalized && _restRepeatStartDate is null)
        {
            return;
        }
        _anchorDate = normalized;
        ClearRestRepeatCore();
        _ignoredRestPromptDate = null;
        OnPropertyChanged(nameof(AnchorDate));
        OnPropertyChanged(nameof(IgnoredRestPromptDate));
        SaveAndNotify(DateTime.Now);
    }

    private void SetCurrentCycleDay(int cycleDay, DateTime today, bool clearRestDecision)
    {
        var normalizedCycleDay = Math.Clamp(cycleDay, 1, CycleLength);
        var anchorDate = new DateTimeOffset(today.Date.AddDays(-(normalizedCycleDay - 1)));
        var restDecisionChanged = clearRestDecision &&
                                  (ClearRestRepeatCore() || _ignoredRestPromptDate is not null);
        if (_currentCycleDay == normalizedCycleDay && _anchorDate == anchorDate && !restDecisionChanged)
        {
            return;
        }

        _currentCycleDay = normalizedCycleDay;
        _anchorDate = anchorDate;
        if (clearRestDecision)
        {
            _ignoredRestPromptDate = null;
        }
        OnPropertyChanged(nameof(CurrentCycleDay));
        OnPropertyChanged(nameof(AnchorDate));
        OnPropertyChanged(nameof(IgnoredRestPromptDate));
        SaveAndNotify(today);
    }

    private bool ClearRestRepeatCore()
    {
        if (_restRepeatStartDate is null && _restRepeatEndDate is null)
        {
            return false;
        }
        _restRepeatStartDate = null;
        _restRepeatEndDate = null;
        OnPropertyChanged(nameof(RestRepeatStartDate));
        OnPropertyChanged(nameof(RestRepeatEndDate));
        return true;
    }

    private void RefreshRotationStatus(DateTime date)
    {
        var index = GetRotationIndex(date);
        if (index < 0)
        {
            RotationStatus = "尚未添加班级批次，请先添加第一批、第二批等全班安排";
            return;
        }

        if (IsContinuousRotation)
        {
            SetField(ref _currentRotationDay, index + 1, nameof(CurrentRotationDay));
        }

        var step = RotationSteps[index];
        var stepName = string.IsNullOrWhiteSpace(step.Name) ? GetDefaultBatchName(index + 1) : step.Name.Trim();
        var source = UseCalendarDateNumber
            ? $"今天是 {date.Day} 日，共 {RotationSteps.Count} 个班级批次，因此全班执行第 {index + 1} 个批次；每月 1 日重新开始"
            : $"从校准日开始按自然日连续计算，今天全班执行第 {index + 1}/{RotationSteps.Count} 个批次；月底不重置，休息日也计数";
        var switchState = IsRotationEnabled ? "自动加载已开启" : "当前仅预览，尚未开启自动加载";
        RotationStatus = $"{source}。今日安排：本班全体执行“{stepName}”对应的时间表（{switchState}）";
    }

    private void SetCurrentRotationDay(int rotationDay)
    {
        var normalizedRotationDay = Math.Clamp(rotationDay, 1, RotationDayMaximum);
        var today = DateTime.Today;
        var anchorDate = new DateTimeOffset(today.AddDays(-(normalizedRotationDay - 1)));
        if (_currentRotationDay == normalizedRotationDay && _rotationAnchorDate == anchorDate)
        {
            return;
        }

        _currentRotationDay = normalizedRotationDay;
        _rotationAnchorDate = anchorDate;
        OnPropertyChanged(nameof(CurrentRotationDay));
        SaveAndNotify(today);
    }

    private int GetRotationIndex(DateTime date)
    {
        if (RotationSteps.Count == 0)
        {
            return -1;
        }
        if (UseCalendarDateNumber)
        {
            return PositiveModulo(date.Day - 1, RotationSteps.Count);
        }
        var elapsedDays = (date.Date - _rotationAnchorDate.Date).Days;
        return PositiveModulo(elapsedDays, RotationSteps.Count);
    }

    private void Load()
    {
        if (!File.Exists(_settingsPath))
        {
            _isLoading = true;
            EnsureInitialBatchSteps();
            _batchTimeLayoutMigrationVersion = 1;
            _isLoading = false;
            Save();
            return;
        }

        var migratedLegacyRotation = false;
        try
        {
            _isLoading = true;
            var model = JsonSerializer.Deserialize<SettingsModel>(File.ReadAllText(_settingsPath), JsonOptions);
            if (model is null)
            {
                return;
            }

            _onboardingVersion = model.OnboardingVersion;
            _isEnabled = HasCompletedOnboarding && model.IsEnabled;
            _anchorDate = new DateTimeOffset(model.AnchorDate.Date);
            _workDays = Math.Clamp(model.WorkDays, 1, 100);
            _restDays = Math.Clamp(model.RestDays, 1, 100);
            _fixedClassPlanId = model.FixedClassPlanId;
            _managedClassPlanIds = model.ManagedClassPlanIds?
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList() ?? [];
            _managedClassPlanId = model.ManagedClassPlanId;
            if (_managedClassPlanIds.Count == 0 && _managedClassPlanId is { } legacyManagedClassPlanId)
            {
                _managedClassPlanIds.Add(legacyManagedClassPlanId);
            }
            _managedClassPlanId = _managedClassPlanIds.FirstOrDefault() is { } firstManagedClassPlanId &&
                                  firstManagedClassPlanId != Guid.Empty
                ? firstManagedClassPlanId
                : null;
            _archivedCyclePlans = model.ArchivedCyclePlans?
                .DistinctBy(item => item.ClassPlanId)
                .ToList() ?? [];
            _managedTimeLayoutId = model.ManagedTimeLayoutId;
            _restRepeatStartDate = model.RestRepeatStartDate?.Date;
            _restRepeatEndDate = model.RestRepeatEndDate?.Date;
            _restRepeatStartDay = Math.Clamp(model.RestRepeatStartDay, 1, _workDays);
            _ignoredRestPromptDate = model.IgnoredRestPromptDate?.Date;
            _isRotationEnabled = model.IsRotationEnabled ?? model.IsDateRotationEnabled ?? false;
            var migrateRotationName = string.IsNullOrWhiteSpace(model.RotationName) || model.RotationName == "轮换事项";
            _rotationName = migrateRotationName ? DefaultRotationName : model.RotationName;
            migratedLegacyRotation |= migrateRotationName;
            _useCalendarDateNumber = model.UseCalendarDateNumber;
            _rotationAnchorDate = new DateTimeOffset(model.RotationAnchorDate.Date);
            _currentRotationDay = Math.Max(1, model.CurrentRotationDay);
            _isRotationSpeechEnabled = model.IsRotationSpeechEnabled;
            _wasDisabledByPlugin = model.WasDisabledByPlugin;

            var rotationSteps = model.RotationSteps;
            if (rotationSteps is null &&
                model.OddDayRotationTime is { } oddTime &&
                model.EvenDayRotationTime is { } evenTime)
            {
                rotationSteps =
                [
                    new RotationStepModel { Name = "原单号设置", Time = oddTime },
                    new RotationStepModel { Name = "原双号设置", Time = evenTime }
                ];
                migratedLegacyRotation = true;
            }
            foreach (var step in rotationSteps ?? [])
            {
                var batchNumber = RotationSteps.Count + 1;
                var rawStepName = step.Name;
                var migrateStepName = string.IsNullOrWhiteSpace(rawStepName) || rawStepName == $"轮换项 {batchNumber}";
                var stepName = migrateStepName
                    ? GetDefaultBatchName(batchNumber)
                    : rawStepName!;
                migratedLegacyRotation |= migrateStepName;
                RotationSteps.Add(new RotationStep(stepName, step.Time, step.TimeLayoutId));
            }

            _batchTimeLayoutMigrationVersion = model.BatchTimeLayoutMigrationVersion;
            if (_batchTimeLayoutMigrationVersion < 1)
            {
                EnsureInitialBatchSteps();
                _isRotationEnabled = true;
                _batchTimeLayoutMigrationVersion = 1;
                migratedLegacyRotation = true;
            }
        }
        catch (JsonException)
        {
            _isLoading = false;
            Save();
            return;
        }
        finally
        {
            _isLoading = false;
        }

        if (migratedLegacyRotation)
        {
            Save();
        }
    }

    private void Save()
    {
        if (_isLoading)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var model = new SettingsModel
        {
            IsEnabled = IsEnabled,
            OnboardingVersion = _onboardingVersion,
            AnchorDate = AnchorDate.Date,
            WorkDays = WorkDays,
            RestDays = RestDays,
            FixedClassPlanId = FixedClassPlanId,
            ManagedClassPlanId = ManagedClassPlanId,
            ManagedClassPlanIds = ManagedClassPlanIds.ToList(),
            ArchivedCyclePlans = ArchivedCyclePlans.ToList(),
            ManagedTimeLayoutId = ManagedTimeLayoutId,
            RestRepeatStartDate = RestRepeatStartDate,
            RestRepeatEndDate = RestRepeatEndDate,
            RestRepeatStartDay = RestRepeatStartDay,
            IgnoredRestPromptDate = IgnoredRestPromptDate,
            IsRotationEnabled = IsRotationEnabled,
            RotationName = RotationName,
            UseCalendarDateNumber = UseCalendarDateNumber,
            RotationAnchorDate = _rotationAnchorDate.Date,
            CurrentRotationDay = CurrentRotationDay,
            RotationSteps = RotationSteps
                .Select(step => new RotationStepModel
                {
                    Name = step.Name,
                    Time = step.Time,
                    TimeLayoutId = step.TimeLayoutId
                })
                .ToList(),
            IsRotationSpeechEnabled = IsRotationSpeechEnabled,
            BatchTimeLayoutMigrationVersion = _batchTimeLayoutMigrationVersion,
            WasDisabledByPlugin = _wasDisabledByPlugin
        };
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(model, JsonOptions));
    }

    private void SaveAndNotify(DateTime date)
    {
        Save();
        RefreshStatus(date);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnRotationStepsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (RotationStep step in e.OldItems)
            {
                step.PropertyChanged -= OnRotationStepPropertyChanged;
            }
        }
        if (e.NewItems is not null)
        {
            foreach (RotationStep step in e.NewItems)
            {
                step.PropertyChanged += OnRotationStepPropertyChanged;
            }
        }

        OnPropertyChanged(nameof(RotationDayMaximum));
        if (_isLoading)
        {
            return;
        }
        if (IsContinuousRotation)
        {
            var normalizedDay = Math.Clamp(_currentRotationDay, 1, RotationDayMaximum);
            _currentRotationDay = normalizedDay;
            _rotationAnchorDate = new DateTimeOffset(DateTime.Today.AddDays(-(normalizedDay - 1)));
            OnPropertyChanged(nameof(CurrentRotationDay));
        }
        SaveAndNotify(DateTime.Now);
    }

    private void OnRotationStepPropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        SaveAndNotify(DateTime.Now);

    private bool SetSetting<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        OnPropertyChanged(propertyName);
        SaveAndNotify(DateTime.Now);
        return true;
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static int PositiveModulo(int value, int divisor) => ((value % divisor) + divisor) % divisor;

    private static TimeSpan RoundUpToFiveMinutes(TimeSpan time)
    {
        var roundedMinutes = Math.Ceiling(time.TotalMinutes / 5) * 5;
        return NormalizeTime(TimeSpan.FromMinutes(roundedMinutes));
    }

    private static TimeSpan NormalizeTime(TimeSpan time) =>
        TimeSpan.FromTicks(PositiveModulo((int)(time.Ticks / TimeSpan.TicksPerSecond), 24 * 60 * 60) * TimeSpan.TicksPerSecond);

    private void EnsureInitialBatchSteps()
    {
        while (RotationSteps.Count < 2)
        {
            var batchNumber = RotationSteps.Count + 1;
            var time = RotationSteps.Count == 0
                ? new TimeSpan(11, 20, 0)
                : NormalizeTime(RotationSteps[^1].Time.Add(TimeSpan.FromMinutes(5)));
            RotationSteps.Add(new RotationStep(GetDefaultBatchName(batchNumber), time));
        }
    }

    private static string GetDefaultBatchName(int batchNumber) => batchNumber switch
    {
        1 => "第一批",
        2 => "第二批",
        3 => "第三批",
        4 => "第四批",
        5 => "第五批",
        _ => $"第 {batchNumber} 批"
    };

    private sealed class SettingsModel
    {
        public bool IsEnabled { get; set; }
        public int OnboardingVersion { get; set; }
        public DateTime AnchorDate { get; set; } = DateTime.Today;
        public int WorkDays { get; set; } = 10;
        public int RestDays { get; set; } = 4;
        public Guid? FixedClassPlanId { get; set; }
        public Guid? ManagedClassPlanId { get; set; }
        public List<Guid>? ManagedClassPlanIds { get; set; }
        public List<ArchivedCyclePlan>? ArchivedCyclePlans { get; set; }
        public Guid? ManagedTimeLayoutId { get; set; }
        public DateTime? RestRepeatStartDate { get; set; }
        public DateTime? RestRepeatEndDate { get; set; }
        public int RestRepeatStartDay { get; set; } = 1;
        public DateTime? IgnoredRestPromptDate { get; set; }
        public bool? IsRotationEnabled { get; set; }
        public string RotationName { get; set; } = DefaultRotationName;
        public bool UseCalendarDateNumber { get; set; } = true;
        public DateTime RotationAnchorDate { get; set; } = DateTime.Today;
        public int CurrentRotationDay { get; set; } = 1;
        public List<RotationStepModel>? RotationSteps { get; set; }
        public bool IsRotationSpeechEnabled { get; set; } = true;
        public int BatchTimeLayoutMigrationVersion { get; set; }
        public bool WasDisabledByPlugin { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IsDateRotationEnabled { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public TimeSpan? OddDayRotationTime { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public TimeSpan? EvenDayRotationTime { get; set; }
    }

    private sealed class RotationStepModel
    {
        public string? Name { get; set; }
        public TimeSpan Time { get; set; }
        public Guid? TimeLayoutId { get; set; }
    }
}
