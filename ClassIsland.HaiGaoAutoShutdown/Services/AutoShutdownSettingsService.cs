using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ClassIsland.HaiGaoAutoShutdown.Models;
using Microsoft.Extensions.Logging;

namespace ClassIsland.HaiGaoAutoShutdown.Services;

public sealed class AutoShutdownSettingsService : INotifyPropertyChanged
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly TimeSpan DefaultShutdownTime = new(22, 0, 0);
    private readonly object _gate = new();
    private readonly string _settingsPath;
    private readonly ILogger<AutoShutdownSettingsService> _logger;
    private bool _isEnabled;
    private TimeSpan _shutdownTime = DefaultShutdownTime;
    private int _countdownSeconds = CountdownLimits.DefaultSeconds;
    private string _nextOccurrenceText = "插件已暂停";
    private string _runtimeStatus = "启用后会在每天指定时间显示可取消的关机倒计时。";

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

    public TimeSpan ShutdownTime
    {
        get
        {
            lock (_gate)
            {
                return _shutdownTime;
            }
        }
        set
        {
            var normalized = value >= TimeSpan.Zero && value < TimeSpan.FromDays(1)
                ? new TimeSpan(value.Hours, value.Minutes, 0)
                : DefaultShutdownTime;
            SetPersisted(ref _shutdownTime, normalized);
        }
    }

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

    public void UpdateNextOccurrence(DateTime? occurrence)
    {
        NextOccurrenceText = occurrence is null
            ? "插件已暂停"
            : $"下一次关机：{occurrence:yyyy 年 M 月 d 日 HH:mm}";
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
            return;
        }

        try
        {
            var data = JsonSerializer.Deserialize<AutoShutdownSettingsData>(
                           File.ReadAllText(_settingsPath),
                           JsonOptions)
                       ?? throw new InvalidDataException("配置内容为空。");
            if (data.ShutdownTime < TimeSpan.Zero ||
                data.ShutdownTime >= TimeSpan.FromDays(1) ||
                data.CountdownSeconds is < CountdownLimits.MinimumSeconds or > CountdownLimits.MaximumSeconds)
            {
                throw new InvalidDataException("配置中的时间或倒计时范围无效。");
            }

            _isEnabled = data.IsEnabled;
            _shutdownTime = new TimeSpan(data.ShutdownTime.Hours, data.ShutdownTime.Minutes, 0);
            _countdownSeconds = data.CountdownSeconds;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "读取定时关机配置失败，插件已安全停用：{SettingsPath}", _settingsPath);
            _isEnabled = false;
            _shutdownTime = DefaultShutdownTime;
            _countdownSeconds = CountdownLimits.DefaultSeconds;
            _runtimeStatus = "配置文件无效，已恢复默认值并安全停用。";
        }
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
        IsEnabled = _isEnabled,
        ShutdownTime = _shutdownTime,
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
