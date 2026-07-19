using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ClassIsland.HaiGaoAutoShutdown.Models;
using Microsoft.Extensions.Logging;

namespace ClassIsland.HaiGaoAutoShutdown.Services;

public sealed class AutoShutdownSettingsService : INotifyPropertyChanged
{
    private const int CurrentSchemaVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly TimeSpan DefaultShutdownTime = new(22, 0, 0);
    private readonly object _gate = new();
    private readonly string _settingsPath;
    private readonly ILogger<AutoShutdownSettingsService> _logger;
    private readonly ObservableCollection<AutoShutdownSchedule> _schedules = [];
    private bool _isEnabled;
    private int _countdownSeconds = CountdownLimits.DefaultSeconds;
    private string _nextOccurrenceText = "插件已暂停";
    private string _runtimeStatus = "启用后会按已开启的多个时间依次显示可取消的关机倒计时。";

    public AutoShutdownSettingsService(
        string settingsPath,
        ILogger<AutoShutdownSettingsService> logger,
        string? legacySettingsPath = null)
    {
        _settingsPath = settingsPath;
        _logger = logger;
        TryImportLegacySettings(legacySettingsPath);
        Load();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? SettingsChanged;

    public bool IsEnabled
    {
        get
        {
            lock (_gate)
            {
                return _isEnabled;
            }
        }
        set => SetPersisted(ref _isEnabled, value);
    }

    public ObservableCollection<AutoShutdownSchedule> Schedules => _schedules;

    public int CountdownSeconds
    {
        get
        {
            lock (_gate)
            {
                return _countdownSeconds;
            }
        }
        set => SetPersisted(ref _countdownSeconds, CountdownLimits.Normalize(value));
    }

    public string NextOccurrenceText
    {
        get => _nextOccurrenceText;
        private set => SetRuntimeField(ref _nextOccurrenceText, value);
    }

    public string RuntimeStatus
    {
        get => _runtimeStatus;
        private set => SetRuntimeField(ref _runtimeStatus, value);
    }

    public AutoShutdownSchedule AddSchedule()
    {
        AutoShutdownSchedule schedule;
        AutoShutdownSettingsData snapshot;
        lock (_gate)
        {
            var nextIndex = _schedules.Count + 1;
            schedule = new AutoShutdownSchedule(
                Guid.NewGuid(),
                $"关机计划 {nextIndex}",
                true,
                GetSuggestedTime());
            AttachSchedule(schedule);
            _schedules.Add(schedule);
            snapshot = CreateSnapshot();
        }

        PersistSnapshot(snapshot, nameof(Schedules));
        return schedule;
    }

    public void RemoveSchedule(AutoShutdownSchedule schedule)
    {
        AutoShutdownSettingsData snapshot;
        lock (_gate)
        {
            if (!_schedules.Remove(schedule))
            {
                return;
            }

            schedule.Changed -= Schedule_OnChanged;
            snapshot = CreateSnapshot();
        }

        PersistSnapshot(snapshot, nameof(Schedules));
    }

    public IReadOnlyList<AutoShutdownScheduleSnapshot> GetEnabledScheduleSnapshot()
    {
        lock (_gate)
        {
            return _schedules
                .Select((schedule, index) => new { Schedule = schedule, Index = index })
                .Where(item => item.Schedule.IsEnabled)
                .Select(item => new AutoShutdownScheduleSnapshot(
                    item.Schedule.Id,
                    string.IsNullOrWhiteSpace(item.Schedule.Name)
                        ? $"关机计划 {item.Index + 1}"
                        : item.Schedule.Name.Trim(),
                    item.Schedule.ShutdownTime))
                .ToArray();
        }
    }

    public void UpdateNextOccurrence(AutoShutdownOccurrence? occurrence)
    {
        if (occurrence is not null)
        {
            NextOccurrenceText = $"下一次关机：{occurrence.ScheduleName} · {occurrence.OccursAt:yyyy 年 M 月 d 日 HH:mm}";
            return;
        }

        bool isEnabled;
        bool hasEnabledSchedule;
        lock (_gate)
        {
            isEnabled = _isEnabled;
            hasEnabledSchedule = _schedules.Any(schedule => schedule.IsEnabled);
        }

        NextOccurrenceText = !isEnabled
            ? "插件已暂停"
            : hasEnabledSchedule
                ? "正在计算下一次关机"
                : "没有启用的关机计划";
    }

    public void UpdateRuntimeStatus(string status) => RuntimeStatus = status;

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
            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.Copy(legacySettingsPath, _settingsPath, false);
            _logger.LogInformation("已从独立定时关机插件导入设置：{LegacySettingsPath}", legacySettingsPath);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "导入独立定时关机插件设置失败：{LegacySettingsPath}", legacySettingsPath);
        }
    }

    private void Load()
    {
        if (!File.Exists(_settingsPath))
        {
            ReplaceSchedules([CreateDefaultScheduleData()]);
            return;
        }

        try
        {
            var data = JsonSerializer.Deserialize<AutoShutdownSettingsData>(
                           File.ReadAllText(_settingsPath),
                           JsonOptions)
                       ?? throw new InvalidDataException("配置内容为空。");
            if (data.CountdownSeconds is < CountdownLimits.MinimumSeconds or > CountdownLimits.MaximumSeconds)
            {
                throw new InvalidDataException("配置中的倒计时范围无效。");
            }

            var migrated = data.Schedules is null;
            var scheduleData = data.Schedules ??
                               [CreateDefaultScheduleData(data.ShutdownTime ?? DefaultShutdownTime)];
            var usedIds = new HashSet<Guid>();
            var normalizedSchedules = new List<AutoShutdownScheduleData>(scheduleData.Count);
            for (var index = 0; index < scheduleData.Count; index++)
            {
                var item = scheduleData[index]
                           ?? throw new InvalidDataException("配置中包含空的关机计划。");
                if (!IsValidTime(item.ShutdownTime))
                {
                    throw new InvalidDataException("配置中包含无效的关机时间。");
                }

                var id = item.Id;
                if (id == Guid.Empty || !usedIds.Add(id))
                {
                    id = Guid.NewGuid();
                    usedIds.Add(id);
                    migrated = true;
                }

                var name = string.IsNullOrWhiteSpace(item.Name)
                    ? $"关机计划 {index + 1}"
                    : item.Name.Trim();
                migrated |= name != item.Name;
                normalizedSchedules.Add(new AutoShutdownScheduleData
                {
                    Id = id,
                    Name = name,
                    IsEnabled = item.IsEnabled,
                    ShutdownTime = NormalizeTime(item.ShutdownTime)
                });
            }

            _isEnabled = data.IsEnabled;
            _countdownSeconds = data.CountdownSeconds;
            ReplaceSchedules(normalizedSchedules);

            if (migrated || data.SchemaVersion < CurrentSchemaVersion)
            {
                if (TrySave(CreateSnapshot()))
                {
                    _runtimeStatus = "旧版单时间配置已迁移为可添加多个关机计划。";
                }
                else
                {
                    _runtimeStatus = "旧配置已载入，但迁移后的多计划配置保存失败。";
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "读取定时关机配置失败，插件已安全停用：{SettingsPath}", _settingsPath);
            _isEnabled = false;
            _countdownSeconds = CountdownLimits.DefaultSeconds;
            ReplaceSchedules([CreateDefaultScheduleData()]);
            _runtimeStatus = "配置文件无效，已恢复默认值并安全停用。";
        }
    }

    private void ReplaceSchedules(IEnumerable<AutoShutdownScheduleData> schedules)
    {
        foreach (var schedule in _schedules)
        {
            schedule.Changed -= Schedule_OnChanged;
        }
        _schedules.Clear();

        foreach (var data in schedules)
        {
            var schedule = new AutoShutdownSchedule(
                data.Id == Guid.Empty ? Guid.NewGuid() : data.Id,
                data.Name,
                data.IsEnabled,
                data.ShutdownTime);
            AttachSchedule(schedule);
            _schedules.Add(schedule);
        }
    }

    private void AttachSchedule(AutoShutdownSchedule schedule) =>
        schedule.Changed += Schedule_OnChanged;

    private void Schedule_OnChanged(object? sender, EventArgs args)
    {
        AutoShutdownSettingsData snapshot;
        lock (_gate)
        {
            snapshot = CreateSnapshot();
        }
        PersistSnapshot(snapshot, nameof(Schedules));
    }

    private TimeSpan GetSuggestedTime()
    {
        if (_schedules.Count == 0)
        {
            return DefaultShutdownTime;
        }

        var next = _schedules[^1].ShutdownTime + TimeSpan.FromHours(1);
        return TimeSpan.FromTicks(next.Ticks % TimeSpan.TicksPerDay);
    }

    private void SetPersisted<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        AutoShutdownSettingsData snapshot;
        lock (_gate)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            snapshot = CreateSnapshot();
        }

        PersistSnapshot(snapshot, propertyName);
    }

    private void PersistSnapshot(AutoShutdownSettingsData snapshot, string? propertyName)
    {
        if (!TrySave(snapshot))
        {
            lock (_gate)
            {
                _isEnabled = false;
            }
            OnPropertyChanged(nameof(IsEnabled));
            RuntimeStatus = "设置保存失败，插件已在本次运行中安全停用。";
        }

        OnPropertyChanged(propertyName);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private AutoShutdownSettingsData CreateSnapshot() => new()
    {
        SchemaVersion = CurrentSchemaVersion,
        IsEnabled = _isEnabled,
        Schedules = _schedules.Select((schedule, index) => schedule.ToData(index)).ToList(),
        ShutdownTime = null,
        CountdownSeconds = _countdownSeconds
    };

    private bool TrySave(AutoShutdownSettingsData data)
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = _settingsPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(data, JsonOptions));
            File.Move(temporaryPath, _settingsPath, true);
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "保存定时关机配置失败：{SettingsPath}", _settingsPath);
            return false;
        }
    }

    private static AutoShutdownScheduleData CreateDefaultScheduleData() =>
        CreateDefaultScheduleData(DefaultShutdownTime);

    private static AutoShutdownScheduleData CreateDefaultScheduleData(TimeSpan shutdownTime) => new()
    {
        Id = Guid.NewGuid(),
        Name = "关机计划 1",
        IsEnabled = true,
        ShutdownTime = NormalizeTime(shutdownTime)
    };

    private static bool IsValidTime(TimeSpan time) =>
        time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);

    private static TimeSpan NormalizeTime(TimeSpan time) =>
        IsValidTime(time)
            ? new TimeSpan(time.Hours, time.Minutes, 0)
            : DefaultShutdownTime;

    private void SetRuntimeField(ref string field, string value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value)
        {
            return;
        }
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
