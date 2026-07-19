using System.Text.Json;
using ClassIsland.HaiGaoAutoShutdown.Models;
using ClassIsland.HaiGaoAutoShutdown.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassIsland.HaiGaoAutoShutdown.Tests;

public sealed class AutoShutdownMigrationTests
{
    [Fact]
    public void LegacySettings_AreImportedIntoIntegratedPluginPath()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var legacyPath = Path.Combine(directory, "legacy", "settings.json");
            var integratedPath = Path.Combine(directory, "integrated", "auto-shutdown.settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
            File.WriteAllText(legacyPath, JsonSerializer.Serialize(new AutoShutdownSettingsData
            {
                IsEnabled = true,
                ShutdownTime = new TimeSpan(21, 35, 0),
                CountdownSeconds = 90
            }));

            var service = new AutoShutdownSettingsService(
                integratedPath,
                NullLogger<AutoShutdownSettingsService>.Instance,
                legacyPath);

            Assert.True(File.Exists(integratedPath));
            Assert.True(service.IsEnabled);
            var migratedSchedule = Assert.Single(service.Schedules);
            Assert.Equal("关机计划 1", migratedSchedule.Name);
            Assert.Equal(new TimeSpan(21, 35, 0), migratedSchedule.ShutdownTime);
            Assert.Equal(90, service.CountdownSeconds);

            var saved = JsonSerializer.Deserialize<AutoShutdownSettingsData>(
                File.ReadAllText(integratedPath));
            Assert.NotNull(saved);
            Assert.Equal(2, saved.SchemaVersion);
            Assert.Null(saved.ShutdownTime);
            Assert.Single(saved.Schedules!);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void MultipleSchedules_AreSavedAndReloaded()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(directory, "settings.json");
            var service = new AutoShutdownSettingsService(
                settingsPath,
                NullLogger<AutoShutdownSettingsService>.Instance);
            var first = Assert.Single(service.Schedules);
            first.Name = "午休";
            first.ShutdownTime = new TimeSpan(12, 30, 0);
            var second = service.AddSchedule();
            second.Name = "晚修";
            second.ShutdownTime = new TimeSpan(22, 15, 0);
            second.IsEnabled = false;
            service.IsEnabled = true;

            var reloaded = new AutoShutdownSettingsService(
                settingsPath,
                NullLogger<AutoShutdownSettingsService>.Instance);

            Assert.True(reloaded.IsEnabled);
            Assert.Collection(
                reloaded.Schedules,
                schedule =>
                {
                    Assert.Equal("午休", schedule.Name);
                    Assert.Equal(new TimeSpan(12, 30, 0), schedule.ShutdownTime);
                    Assert.True(schedule.IsEnabled);
                },
                schedule =>
                {
                    Assert.Equal("晚修", schedule.Name);
                    Assert.Equal(new TimeSpan(22, 15, 0), schedule.ShutdownTime);
                    Assert.False(schedule.IsEnabled);
                });
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void EmptyScheduleList_RemainsEmptyAfterReload()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(directory, "settings.json");
            var service = new AutoShutdownSettingsService(
                settingsPath,
                NullLogger<AutoShutdownSettingsService>.Instance);
            service.RemoveSchedule(Assert.Single(service.Schedules));

            var reloaded = new AutoShutdownSettingsService(
                settingsPath,
                NullLogger<AutoShutdownSettingsService>.Instance);

            Assert.Empty(reloaded.Schedules);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void LegacyState_AreImportedIntoIntegratedPluginPath()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var occurrence = new DateTime(2026, 7, 18, 22, 0, 0);
            var legacyPath = Path.Combine(directory, "legacy", "state.json");
            var integratedPath = Path.Combine(directory, "integrated", "auto-shutdown.state.json");
            Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
            File.WriteAllText(legacyPath, JsonSerializer.Serialize(new AutoShutdownStateData
            {
                LastHandledOccurrence = occurrence,
                LastOutcome = "Cancelled"
            }));

            var store = new AutoShutdownStateStore(
                integratedPath,
                NullLogger<AutoShutdownStateStore>.Instance,
                legacyPath);

            Assert.True(File.Exists(integratedPath));
            Assert.Equal(occurrence, store.LastHandledOccurrence);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"HaiGaoAutoShutdownTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
