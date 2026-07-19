using System.ComponentModel;
using System.Text.Json;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.HaiGao104.Abstractions;
using ClassIsland.HaiGaoDuty.Models;
using ClassIsland.HaiGaoDuty.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClassIsland.HaiGaoDuty.Tests;

public sealed class DutyRosterServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [Fact]
    public void LegacySettingsAreCopiedOnlyWhenIntegratedSettingsAreMissing()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var legacyPath = Path.Combine(root, "legacy", "settings.json");
            var integratedPath = Path.Combine(root, "integrated", "duty-roster.settings.json");
            WriteState(legacyPath, new DutyRosterPersistentState
            {
                IsEnabled = true,
                ReminderTime = new TimeSpan(7, 31, 0),
                IsSpeechEnabled = false,
                ActiveConfiguration = new DutyRosterConfiguration
                {
                    Names = ["A", "B"],
                    DailyCount = 1
                },
                CurrentOrder = ["A", "B"],
                CurrentOrderCursor = 1,
                CycleNumber = 2,
                LastProcessedDate = new DateTime(2026, 7, 18),
                AssignmentDate = new DateTime(2026, 7, 18),
                TodayAssignees = ["A"]
            });

            var imported = CreateService(
                integratedPath,
                new FakeCycleCalendar(),
                new DateTime(2026, 7, 18, 7, 0, 0),
                legacyPath);

            Assert.True(imported.IsEnabled);
            Assert.Equal(new TimeSpan(7, 31, 0), imported.ReminderTime);
            Assert.False(imported.IsSpeechEnabled);
            Assert.True(File.Exists(legacyPath));
            Assert.True(File.Exists(integratedPath));
            var migratedState = ReadState(integratedPath);
            Assert.Equal(["A", "B"], migratedState.ActiveConfiguration!.Names);
            Assert.Equal(2, migratedState.CycleNumber);
            Assert.Equal(1, migratedState.CurrentOrderCursor);
            Assert.Equal(new DateTime(2026, 7, 18), migratedState.AssignmentDate);
            Assert.Equal(["A"], migratedState.TodayAssignees);

            WriteState(integratedPath, new DutyRosterPersistentState
            {
                IsEnabled = false,
                ReminderTime = new TimeSpan(9, 15, 0),
                IsSpeechEnabled = true
            });

            var destinationWins = CreateService(
                integratedPath,
                new FakeCycleCalendar(),
                new DateTime(2026, 7, 18, 7, 0, 0),
                legacyPath);

            Assert.False(destinationWins.IsEnabled);
            Assert.Equal(new TimeSpan(9, 15, 0), destinationWins.ReminderTime);
            Assert.True(destinationWins.IsSpeechEnabled);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void FirstSuccessfulRosterApplicationAutomaticallyEnablesRotation()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(root, "duty-roster.settings.json");
            var service = CreateService(
                settingsPath,
                new FakeCycleCalendar(),
                new DateTime(2026, 7, 18, 7, 0, 0));
            Assert.False(service.IsEnabled);
            service.RosterDraft = "A\nB\nC";
            service.DailyCountDraft = 1;

            service.ApplyDraft();

            Assert.True(service.IsEnabled);
            Assert.Contains("自动开启", service.ValidationMessage);
            Assert.True(ReadState(settingsPath).IsEnabled);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void PausedCycleSkipsDatesWithoutConsumingOrCatchingUp()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(root, "duty-roster.settings.json");
            var calendar = new FakeCycleCalendar();
            var service = CreateService(
                settingsPath,
                calendar,
                new DateTime(2026, 7, 18, 7, 0, 0));
            service.RosterDraft = "A\nB\nC\nD";
            service.DailyCountDraft = 1;
            service.ApplyDraft();
            Assert.Equal(["A"], ReadState(settingsPath).TodayAssignees);

            calendar.IsCycleActive = false;
            service.Refresh(new DateTime(2026, 7, 21, 7, 0, 0));

            var pausedState = ReadState(settingsPath);
            Assert.Equal(new DateTime(2026, 7, 21), pausedState.LastProcessedDate);
            Assert.Equal(1, pausedState.CurrentOrderCursor);
            Assert.Equal(new DateTime(2026, 7, 18), pausedState.AssignmentDate);
            Assert.Equal("HL Education + 课表已暂停", service.TodayDisplay);

            calendar.IsCycleActive = true;
            service.Refresh(new DateTime(2026, 7, 21, 7, 1, 0));

            var resumedState = ReadState(settingsPath);
            Assert.Equal(2, resumedState.CurrentOrderCursor);
            Assert.Equal(new DateTime(2026, 7, 21), resumedState.AssignmentDate);
            Assert.Equal(["B"], resumedState.TodayAssignees);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void PausedCycleDoesNotReserveReminder()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(root, "duty-roster.settings.json");
            var calendar = new FakeCycleCalendar();
            var service = CreateService(
                settingsPath,
                calendar,
                new DateTime(2026, 7, 18, 7, 0, 0));
            service.RosterDraft = "A\nB";
            service.ApplyDraft();
            calendar.IsCycleActive = false;

            var isDue = service.TryMarkReminderDue(
                new DateTime(2026, 7, 18, 8, 0, 0),
                out var assignees);

            Assert.False(isDue);
            Assert.Empty(assignees);
            Assert.Null(ReadState(settingsPath).LastNotifiedDate);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static DutyRosterService CreateService(
        string settingsPath,
        ICycleCalendar calendar,
        DateTime now,
        string? legacySettingsPath = null) =>
        new(
            settingsPath,
            calendar,
            new FakeExactTimeService(now),
            null!,
            NullLogger<DutyRosterService>.Instance,
            new IdentityShuffleSource(),
            legacySettingsPath);

    private static DutyRosterPersistentState ReadState(string path) =>
        JsonSerializer.Deserialize<DutyRosterPersistentState>(File.ReadAllText(path), JsonOptions)!;

    private static void WriteState(string path, DutyRosterPersistentState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOptions));
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"duty-roster-service-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeCycleCalendar : ICycleCalendar
    {
        public bool IsCycleActive { get; set; } = true;

        public bool IsRestDay(DateTime date) => false;

        public event EventHandler? SettingsChanged
        {
            add { }
            remove { }
        }
    }

    private sealed class FakeExactTimeService(DateTime now) : IExactTimeService
    {
        public string SyncStatusMessage { get; set; } = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged
        {
            add { }
            remove { }
        }

        public event PropertyChangingEventHandler? PropertyChanging
        {
            add { }
            remove { }
        }

        public void Sync()
        {
        }

        public DateTime GetCurrentLocalDateTime() => now;
    }

    private sealed class IdentityShuffleSource : IShuffleSource
    {
        public int Next(int maxExclusive) => maxExclusive - 1;
    }
}
