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
            Assert.Equal(new TimeSpan(21, 35, 0), service.ShutdownTime);
            Assert.Equal(90, service.CountdownSeconds);
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
