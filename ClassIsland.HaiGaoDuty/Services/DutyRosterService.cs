using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.HaiGao104.Abstractions;
using ClassIsland.HaiGaoDuty.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassIsland.HaiGaoDuty.Services;

public sealed class DutyRosterService : INotifyPropertyChanged, IHostedService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly string _settingsPath;
    private readonly ICycleCalendar? _cycleCalendar;
    private readonly IExactTimeService _exactTimeService;
    private readonly ILessonsService _lessonsService;
    private readonly ILogger<DutyRosterService> _logger;
    private readonly DutyRosterEngine _engine;
    private DutyRosterPersistentState _state;
    private string _rosterDraft = string.Empty;
    private int _dailyCountDraft = 1;
    private string _todayDisplay = "正在计算今日值日生…";
    private string _todayStatus = string.Empty;
    private string _cycleProgress = string.Empty;
    private string _pendingStatus = "没有待生效修改";
    private string _validationMessage = string.Empty;
    private bool _isStarted;

    public DutyRosterService(
        string settingsPath,
        ICycleCalendar? cycleCalendar,
        IExactTimeService exactTimeService,
        ILessonsService lessonsService,
        ILogger<DutyRosterService> logger,
        string? legacySettingsPath = null)
        : this(
            settingsPath,
            cycleCalendar,
            exactTimeService,
            lessonsService,
            logger,
            new CryptoShuffleSource(),
            legacySettingsPath)
    {
    }

    internal DutyRosterService(
        string settingsPath,
        ICycleCalendar? cycleCalendar,
        IExactTimeService exactTimeService,
        ILessonsService lessonsService,
        ILogger<DutyRosterService> logger,
        IShuffleSource shuffleSource,
        string? legacySettingsPath = null)
    {
        _settingsPath = settingsPath;
        _cycleCalendar = cycleCalendar;
        _exactTimeService = exactTimeService;
        _lessonsService = lessonsService;
        _logger = logger;
        _engine = new DutyRosterEngine(shuffleSource);
        TryImportLegacySettings(legacySettingsPath);
        _state = LoadState();
        NormalizeLoadedState();
        ResetDraftFromSavedConfiguration();
        UpdateDisplay(_exactTimeService.GetCurrentLocalDateTime());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsEnabled
    {
        get => _state.IsEnabled;
        set
        {
            lock (_gate)
            {
                if (_state.IsEnabled == value)
                {
                    return;
                }
                var previous = _state.IsEnabled;
                _state.IsEnabled = value;
                if (!SaveState())
                {
                    _state.IsEnabled = previous;
                    ValidationMessage = "保存失败，启用状态未更改。";
                }
                OnPropertyChanged();
                UpdateDisplay(_exactTimeService.GetCurrentLocalDateTime());
            }
        }
    }

    public TimeSpan ReminderTime
    {
        get => _state.ReminderTime;
        set
        {
            var normalized = value >= TimeSpan.Zero && value < TimeSpan.FromDays(1)
                ? value
                : new TimeSpan(7, 30, 0);
            lock (_gate)
            {
                if (_state.ReminderTime == normalized)
                {
                    return;
                }
                var previous = _state.ReminderTime;
                _state.ReminderTime = normalized;
                if (!SaveState())
                {
                    _state.ReminderTime = previous;
                    ValidationMessage = "保存失败，提醒时间未更改。";
                }
                OnPropertyChanged();
            }
        }
    }

    public bool IsSpeechEnabled
    {
        get => _state.IsSpeechEnabled;
        set
        {
            lock (_gate)
            {
                if (_state.IsSpeechEnabled == value)
                {
                    return;
                }
                var previous = _state.IsSpeechEnabled;
                _state.IsSpeechEnabled = value;
                if (!SaveState())
                {
                    _state.IsSpeechEnabled = previous;
                    ValidationMessage = "保存失败，语音设置未更改。";
                }
                OnPropertyChanged();
            }
        }
    }

    public string RosterDraft
    {
        get => _rosterDraft;
        set => SetField(ref _rosterDraft, value ?? string.Empty);
    }

    public int DailyCountDraft
    {
        get => _dailyCountDraft;
        set => SetField(ref _dailyCountDraft, Math.Max(1, value));
    }

    public string TodayDisplay
    {
        get => _todayDisplay;
        private set => SetField(ref _todayDisplay, value);
    }

    public string TodayStatus
    {
        get => _todayStatus;
        private set => SetField(ref _todayStatus, value);
    }

    public string CycleProgress
    {
        get => _cycleProgress;
        private set => SetField(ref _cycleProgress, value);
    }

    public string PendingStatus
    {
        get => _pendingStatus;
        private set => SetField(ref _pendingStatus, value);
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        private set => SetField(ref _validationMessage, value);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_isStarted)
            {
                return Task.CompletedTask;
            }
            _isStarted = true;
            _lessonsService.PostMainTimerTicked += OnMainTimerTicked;
            if (_cycleCalendar is not null)
            {
                _cycleCalendar.SettingsChanged += OnCalendarSettingsChanged;
            }
        }
        Refresh(_exactTimeService.GetCurrentLocalDateTime());
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_isStarted)
            {
                return Task.CompletedTask;
            }
            _isStarted = false;
            _lessonsService.PostMainTimerTicked -= OnMainTimerTicked;
            if (_cycleCalendar is not null)
            {
                _cycleCalendar.SettingsChanged -= OnCalendarSettingsChanged;
            }
            SaveState();
        }
        return Task.CompletedTask;
    }

    public void ApplyDraft()
    {
        lock (_gate)
        {
            var previousState = CloneState(_state);
            var previousRosterDraft = RosterDraft;
            var previousDailyCountDraft = DailyCountDraft;
            var shouldNotifyEnabled = false;
            var parsed = DutyRosterEngine.ParseRoster(RosterDraft);
            if (parsed.DuplicateName is not null)
            {
                ValidationMessage = $"名单中存在重复姓名“{parsed.DuplicateName}”；同名学生请添加区分后缀。";
                return;
            }

            var configuration = new DutyRosterConfiguration
            {
                Names = [.. parsed.Names],
                DailyCount = DailyCountDraft
            };
            var validationError = _engine.ValidatePendingConfiguration(_state, configuration);
            if (validationError is not null)
            {
                ValidationMessage = validationError;
                return;
            }

            if (_state.ActiveConfiguration is null ||
                (_state.CurrentOrder.Count == 0 && _state.AssignmentDate is null))
            {
                shouldNotifyEnabled = !_state.IsEnabled;
                _engine.ActivateInitialConfiguration(_state, configuration);
                _state.IsEnabled = true;
                ValidationMessage = shouldNotifyEnabled
                    ? "名单已启用，值日生轮换已自动开启。"
                    : "名单已启用。";
                TryAssignCurrentDate(_exactTimeService.GetCurrentLocalDateTime());
            }
            else if (DutyRosterEngine.ConfigurationEquals(_state.ActiveConfiguration, configuration))
            {
                _state.PendingConfiguration = null;
                ValidationMessage = "当前名单与人数保持不变，已取消待生效修改。";
            }
            else
            {
                _state.PendingConfiguration = configuration.Clone();
                ValidationMessage = "修改已保存，将在当前轮次结束后生效。";
            }

            if (!SaveState())
            {
                _state = previousState;
                RosterDraft = previousRosterDraft;
                DailyCountDraft = previousDailyCountDraft;
                ValidationMessage = "保存失败，名单与人数修改未应用。";
            }
            else if (shouldNotifyEnabled)
            {
                OnPropertyChanged(nameof(IsEnabled));
            }
            UpdateDisplay(_exactTimeService.GetCurrentLocalDateTime());
        }
    }

    public void CancelPendingConfiguration()
    {
        lock (_gate)
        {
            var previousState = CloneState(_state);
            _state.PendingConfiguration = null;
            ResetDraftFromSavedConfiguration();
            ValidationMessage = "已取消待生效修改。";
            if (!SaveState())
            {
                _state = previousState;
                ResetDraftFromSavedConfiguration();
                ValidationMessage = "保存失败，待生效修改未取消。";
            }
            UpdateDerivedStatus();
        }
    }

    public void ImportRoster(string filePath)
    {
        RosterImportResult result;
        try
        {
            result = RosterImportService.ImportFile(filePath);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "读取值日生名单文件失败：{FilePath}", filePath);
            ValidationMessage = "读取名单失败，请确认文件没有损坏或被其他程序占用。";
            return;
        }

        lock (_gate)
        {
            if (result.Names.Count == 0)
            {
                ValidationMessage = result.Message;
                return;
            }

            RosterDraft = string.Join(Environment.NewLine, result.Names);
            ValidationMessage = $"{result.Message} 已填入编辑框，请检查后再点击“应用名单与人数”。";
        }
    }

    public void Refresh(DateTime now)
    {
        lock (_gate)
        {
            if (_cycleCalendar is null)
            {
                UpdateDisplay(now);
                return;
            }

            if (!_cycleCalendar.IsCycleActive)
            {
                var pausedPreviousState = CloneState(_state);
                var pausedDate = now.Date;
                if (_state.LastProcessedDate is null || pausedDate > _state.LastProcessedDate.Value.Date)
                {
                    _state.LastProcessedDate = pausedDate;
                    if (!SaveState())
                    {
                        _state = pausedPreviousState;
                        UpdateDisplay(now);
                        TodayStatus = "无法保存暂停日期，值日生将在下一次刷新时重试。";
                        return;
                    }
                }
                UpdateDisplay(now);
                return;
            }

            var previousState = CloneState(_state);
            var previousRosterDraft = RosterDraft;
            var previousDailyCountDraft = DailyCountDraft;
            var today = now.Date;
            var changed = false;
            if (_state.LastProcessedDate is null)
            {
                _state.LastProcessedDate = today;
                changed = true;
                if (!_cycleCalendar.IsRestDay(today) && _state.ActiveConfiguration is not null)
                {
                    ProcessDutyDay(today);
                }
            }
            else if (today > _state.LastProcessedDate.Value.Date)
            {
                for (var date = _state.LastProcessedDate.Value.Date.AddDays(1);
                     date <= today;
                     date = date.AddDays(1))
                {
                    if (!_cycleCalendar.IsRestDay(date) && _state.ActiveConfiguration is not null)
                    {
                        ProcessDutyDay(date);
                    }
                }
                _state.LastProcessedDate = today;
                changed = true;
            }
            else if (today == _state.LastProcessedDate.Value.Date &&
                     !_cycleCalendar.IsRestDay(today) &&
                     _state.ActiveConfiguration is not null &&
                     _state.AssignmentDate?.Date != today)
            {
                ProcessDutyDay(today);
                changed = true;
            }

            if (changed && !SaveState())
            {
                _state = previousState;
                RosterDraft = previousRosterDraft;
                DailyCountDraft = previousDailyCountDraft;
                UpdateDisplay(now);
                TodayStatus = "无法保存轮换进度，本次推进已撤销；插件将在下一次刷新时重试。";
                return;
            }
            UpdateDisplay(now);
        }
    }

    public bool TryMarkReminderDue(DateTime now, out IReadOnlyList<string> assignees)
    {
        Refresh(now);
        lock (_gate)
        {
            assignees = [];
            var today = now.Date;
            if (!_state.IsEnabled ||
                _cycleCalendar is null ||
                !_cycleCalendar.IsCycleActive ||
                _cycleCalendar.IsRestDay(today) ||
                _state.AssignmentDate?.Date != today ||
                _state.TodayAssignees.Count == 0 ||
                now.TimeOfDay < _state.ReminderTime ||
                _state.LastNotifiedDate?.Date == today)
            {
                return false;
            }

            var previousNotifiedDate = _state.LastNotifiedDate;
            _state.LastNotifiedDate = today;
            if (!SaveState())
            {
                _state.LastNotifiedDate = previousNotifiedDate;
                return false;
            }
            assignees = [.. _state.TodayAssignees];
            return true;
        }
    }

    public void ReleaseReminderReservation(DateTime date)
    {
        lock (_gate)
        {
            if (_state.LastNotifiedDate?.Date != date.Date)
            {
                return;
            }
            _state.LastNotifiedDate = null;
            SaveState();
        }
    }

    private void OnMainTimerTicked(object? sender, EventArgs e) =>
        Refresh(_exactTimeService.GetCurrentLocalDateTime());

    private void OnCalendarSettingsChanged(object? sender, EventArgs e) =>
        Refresh(_exactTimeService.GetCurrentLocalDateTime());

    private void TryAssignCurrentDate(DateTime now)
    {
        if (_cycleCalendar is null ||
            !_cycleCalendar.IsCycleActive ||
            _cycleCalendar.IsRestDay(now.Date) ||
            _state.ActiveConfiguration is null)
        {
            return;
        }

        ProcessDutyDay(now.Date);
        _state.LastProcessedDate = now.Date;
    }

    private void ProcessDutyDay(DateTime date)
    {
        var hadPendingConfiguration = _state.PendingConfiguration is not null;
        var assignment = _engine.AssignNextDutyDay(_state);
        _state.AssignmentDate = date.Date;
        _state.TodayAssignees = [.. assignment.Names];
        TodayStatus = assignment.Warning ?? $"已为 {date:yyyy 年 M 月 d 日}生成值日生";
        if (hadPendingConfiguration && _state.PendingConfiguration is null)
        {
            ResetDraftFromSavedConfiguration();
        }
    }

    private void UpdateDisplay(DateTime now)
    {
        if (_cycleCalendar is null)
        {
            TodayDisplay = "依赖服务不可用";
            TodayStatus = "海亮教育+内部周期服务尚未就绪，请重新启用插件或重启 ClassIsland。";
            UpdateDerivedStatus();
            return;
        }

        if (!_cycleCalendar.IsCycleActive)
        {
            TodayDisplay = "海亮教育+课表已暂停";
            TodayStatus = "值日生不会推进或提醒；恢复完全接管后从当天继续。";
            UpdateDerivedStatus();
            return;
        }

        if (now.Date < _state.LastProcessedDate?.Date)
        {
            TodayDisplay = "系统日期异常";
            TodayStatus = $"当前日期早于最后处理日期 {_state.LastProcessedDate:yyyy-MM-dd}，请先校准系统时间。";
            UpdateDerivedStatus();
            return;
        }

        if (!_state.IsEnabled)
        {
            TodayDisplay = "值日生已停用";
            TodayStatus = "轮换仍按实际上课日推进，重新启用后会显示当天结果。";
            UpdateDerivedStatus();
            return;
        }

        if (_cycleCalendar.IsRestDay(now.Date))
        {
            TodayDisplay = "今日休息";
            TodayStatus = "休息日不安排值日生，也不消耗当前名单。";
            UpdateDerivedStatus();
            return;
        }

        if (_state.ActiveConfiguration is null)
        {
            TodayDisplay = "未配置值日生";
            TodayStatus = "请在“适用于海亮教育的大周值日生”设置页录入名单。";
            UpdateDerivedStatus();
            return;
        }

        if (_state.AssignmentDate?.Date == now.Date && _state.TodayAssignees.Count > 0)
        {
            TodayDisplay = string.Join("、", _state.TodayAssignees);
            if (string.IsNullOrWhiteSpace(TodayStatus))
            {
                TodayStatus = $"已为 {now:yyyy 年 M 月 d 日}生成值日生";
            }
        }
        else
        {
            TodayDisplay = "正在生成今日值日生…";
            TodayStatus = "等待 ClassIsland 主计时器刷新。";
        }

        UpdateDerivedStatus();
    }

    private void UpdateDerivedStatus()
    {
        var active = _state.ActiveConfiguration;
        CycleProgress = active is null
            ? "尚未开始轮换"
            : _state.CurrentOrder.Count == 0
                ? "等待开始第 1 轮"
                : _state.CurrentOrderCursor >= _state.CurrentOrder.Count
                    ? $"第 {_state.CycleNumber} 轮已完成：{_state.CurrentOrder.Count}/{_state.CurrentOrder.Count} 人"
                    : $"第 {_state.CycleNumber} 轮：已安排 {_state.CurrentOrderCursor}/{_state.CurrentOrder.Count} 人";

        PendingStatus = _state.PendingConfiguration is null
            ? "没有待生效修改"
            : $"已有待生效修改：{_state.PendingConfiguration.Names.Count} 人，" +
              $"每天 {_state.PendingConfiguration.DailyCount} 人；将在当前轮次结束时切换。";
    }

    private void ResetDraftFromSavedConfiguration()
    {
        var source = _state.PendingConfiguration ?? _state.ActiveConfiguration;
        RosterDraft = source is null ? string.Empty : string.Join(Environment.NewLine, source.Names);
        DailyCountDraft = source?.DailyCount ?? 1;
        UpdateDerivedStatus();
    }

    private void TryImportLegacySettings(string? legacySettingsPath)
    {
        if (File.Exists(_settingsPath) ||
            string.IsNullOrWhiteSpace(legacySettingsPath) ||
            !File.Exists(legacySettingsPath))
        {
            return;
        }

        try
        {
            var settingsDirectory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrWhiteSpace(settingsDirectory))
            {
                Directory.CreateDirectory(settingsDirectory);
            }
            File.Copy(legacySettingsPath, _settingsPath, false);
            _logger.LogInformation(
                "已从旧版独立值日生插件迁移配置：{LegacySettingsPath}",
                legacySettingsPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                exception,
                "无法从旧版独立值日生插件迁移配置，将保留旧文件并使用当前配置。");
        }
    }

    private DutyRosterPersistentState LoadState()
    {
        if (!File.Exists(_settingsPath))
        {
            return new DutyRosterPersistentState();
        }

        try
        {
            return JsonSerializer.Deserialize<DutyRosterPersistentState>(
                       File.ReadAllText(_settingsPath), JsonOptions) ??
                   new DutyRosterPersistentState();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "无法读取海高值日生配置，将保留损坏文件并使用默认配置。");
            TryPreserveCorruptedSettings();
            return new DutyRosterPersistentState();
        }
    }

    private void NormalizeLoadedState()
    {
        DutyRosterStateRepair.RepairDeserializedCollections(_state);
        _state.ReminderTime = _state.ReminderTime >= TimeSpan.Zero &&
                              _state.ReminderTime < TimeSpan.FromDays(1)
            ? _state.ReminderTime
            : new TimeSpan(7, 30, 0);
        _state.LastProcessedDate = _state.LastProcessedDate?.Date;
        _state.AssignmentDate = _state.AssignmentDate?.Date;
        _state.LastNotifiedDate = _state.LastNotifiedDate?.Date;
        _state.CycleNumber = Math.Max(0, _state.CycleNumber);

        if (_state.ActiveConfiguration is not null &&
            DutyRosterEngine.ValidateConfiguration(_state.ActiveConfiguration) is not null)
        {
            _state.ActiveConfiguration = null;
        }
        if (_state.PendingConfiguration is not null &&
            DutyRosterEngine.ValidateConfiguration(_state.PendingConfiguration) is not null)
        {
            _state.PendingConfiguration = null;
        }

        if (_state.ActiveConfiguration is null || !IsCurrentOrderValid())
        {
            _state.CurrentOrder = [];
            _state.CurrentOrderCursor = 0;
            _state.CycleNumber = 0;
            _state.AssignmentDate = null;
            _state.TodayAssignees = [];
        }
        else
        {
            _state.CurrentOrderCursor = Math.Clamp(
                _state.CurrentOrderCursor, 0, _state.CurrentOrder.Count);
        }
    }

    private bool IsCurrentOrderValid()
    {
        var names = _state.ActiveConfiguration?.Names;
        return names is not null &&
               _state.CurrentOrder.Count == names.Count &&
               _state.CurrentOrder.ToHashSet(StringComparer.OrdinalIgnoreCase)
                   .SetEquals(names);
    }

    private static DutyRosterPersistentState CloneState(DutyRosterPersistentState state) => new()
    {
        SchemaVersion = state.SchemaVersion,
        IsEnabled = state.IsEnabled,
        ReminderTime = state.ReminderTime,
        IsSpeechEnabled = state.IsSpeechEnabled,
        ActiveConfiguration = state.ActiveConfiguration?.Clone(),
        PendingConfiguration = state.PendingConfiguration?.Clone(),
        CurrentOrder = [.. state.CurrentOrder],
        CurrentOrderCursor = state.CurrentOrderCursor,
        CycleNumber = state.CycleNumber,
        LastProcessedDate = state.LastProcessedDate,
        AssignmentDate = state.AssignmentDate,
        TodayAssignees = [.. state.TodayAssignees],
        LastNotifiedDate = state.LastNotifiedDate
    };

    private bool SaveState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var temporaryPath = _settingsPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_state, JsonOptions));
            File.Move(temporaryPath, _settingsPath, true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "无法保存海高值日生配置。");
            return false;
        }
    }

    private void TryPreserveCorruptedSettings()
    {
        try
        {
            var backupPath = _settingsPath + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json";
            File.Move(_settingsPath, backupPath, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "无法备份损坏的海高值日生配置。");
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
