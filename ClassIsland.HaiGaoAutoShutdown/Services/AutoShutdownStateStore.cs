using System.Text.Json;
using ClassIsland.HaiGaoAutoShutdown.Models;
using Microsoft.Extensions.Logging;

namespace ClassIsland.HaiGaoAutoShutdown.Services;

public sealed class AutoShutdownStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _statePath;
    private readonly ILogger<AutoShutdownStateStore> _logger;
    private AutoShutdownStateData _state;

    public AutoShutdownStateStore(
        string statePath,
        ILogger<AutoShutdownStateStore> logger,
        string? legacyStatePath = null)
    {
        _statePath = statePath;
        _logger = logger;
        TryImportLegacyState(legacyStatePath);
        _state = Load();
    }

    public DateTime? LastHandledOccurrence
    {
        get
        {
            lock (_gate)
            {
                return _state.LastHandledOccurrence;
            }
        }
    }

    public void MarkHandled(DateTime occurrence, string outcome)
    {
        AutoShutdownStateData snapshot;
        lock (_gate)
        {
            _state.LastHandledOccurrence = occurrence;
            _state.LastOutcome = outcome;
            snapshot = new AutoShutdownStateData
            {
                LastHandledOccurrence = occurrence,
                LastOutcome = outcome
            };
        }
        Save(snapshot);
    }

    private void TryImportLegacyState(string? legacyStatePath)
    {
        if (File.Exists(_statePath) ||
            string.IsNullOrWhiteSpace(legacyStatePath) ||
            !File.Exists(legacyStatePath))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.Copy(legacyStatePath, _statePath, false);
            _logger.LogInformation("已从独立定时关机插件导入运行状态：{LegacyStatePath}", legacyStatePath);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "导入独立定时关机插件运行状态失败：{LegacyStatePath}", legacyStatePath);
        }
    }

    private AutoShutdownStateData Load()
    {
        if (!File.Exists(_statePath))
        {
            return new AutoShutdownStateData();
        }

        try
        {
            return JsonSerializer.Deserialize<AutoShutdownStateData>(
                       File.ReadAllText(_statePath),
                       JsonOptions)
                   ?? new AutoShutdownStateData();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "读取定时关机运行状态失败，将从空状态继续：{StatePath}", _statePath);
            return new AutoShutdownStateData();
        }
    }

    private void Save(AutoShutdownStateData state)
    {
        try
        {
            var directory = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = _statePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temporaryPath, _statePath, true);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "保存定时关机运行状态失败：{StatePath}", _statePath);
        }
    }
}
