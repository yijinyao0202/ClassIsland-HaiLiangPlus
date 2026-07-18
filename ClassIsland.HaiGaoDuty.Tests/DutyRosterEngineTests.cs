using System.Text.Json;
using System.IO.Compression;
using ClassIsland.HaiGaoDuty.Models;
using ClassIsland.HaiGaoDuty.Services;
using Xunit;

namespace ClassIsland.HaiGaoDuty.Tests;

public sealed class DutyRosterEngineTests
{
    [Fact]
    public void EachCycleContainsEveryStudentExactlyOnce()
    {
        var engine = new DutyRosterEngine(new ScriptedShuffleSource());
        var state = Activate(engine, ["A", "B", "C", "D"], dailyCount: 1);

        var assignments = Enumerable.Range(0, 8)
            .Select(_ => AssertSuccessful(engine.AssignNextDutyDay(state)).Single())
            .ToArray();

        AssertCycle(assignments[..4], ["A", "B", "C", "D"]);
        AssertCycle(assignments[4..], ["A", "B", "C", "D"]);
        Assert.Equal(2, state.CycleNumber);
    }

    [Fact]
    public void MultiPersonAssignmentFillsAcrossCyclesWhenRosterDoesNotDivideDailyCount()
    {
        var engine = new DutyRosterEngine(new ScriptedShuffleSource());
        var state = Activate(engine, ["A", "B", "C", "D", "E"], dailyCount: 3);

        var firstDay = AssertSuccessful(engine.AssignNextDutyDay(state));
        var secondDay = AssertSuccessful(engine.AssignNextDutyDay(state));

        Assert.Equal(["A", "B", "C"], firstDay);
        Assert.Equal(["D", "E", "A"], secondDay);
        Assert.Equal(2, state.CycleNumber);
        Assert.Equal(1, state.CurrentOrderCursor);
    }

    [Fact]
    public void NewCycleMovesSameDayCollisionBehindUnusedStudents()
    {
        var shuffle = new ScriptedShuffleSource(
            3, 2, 1, // First cycle: A, B, C, D.
            2, 1, 0  // Second cycle before adjustment: D, A, B, C.
        );
        var engine = new DutyRosterEngine(shuffle);
        var state = Activate(engine, ["A", "B", "C", "D"], dailyCount: 3);

        Assert.Equal(["A", "B", "C"], AssertSuccessful(engine.AssignNextDutyDay(state)));
        var crossingDay = AssertSuccessful(engine.AssignNextDutyDay(state));

        Assert.Equal(["D", "A", "B"], crossingDay);
        Assert.Equal(crossingDay.Count, crossingDay.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(6, shuffle.CallCount);
    }

    [Fact]
    public void PersistedOrderAndCursorContinueAfterRestartWithoutReshuffling()
    {
        var engine = new DutyRosterEngine(new ScriptedShuffleSource());
        var state = Activate(engine, ["A", "B", "C", "D", "E"], dailyCount: 2);
        Assert.Equal(["A", "B"], AssertSuccessful(engine.AssignNextDutyDay(state)));

        var persistedOrder = state.CurrentOrder.ToArray();
        var json = JsonSerializer.Serialize(state);
        var restored = JsonSerializer.Deserialize<DutyRosterPersistentState>(json);
        Assert.NotNull(restored);

        var restartedEngine = new DutyRosterEngine(new ThrowingShuffleSource());
        var nextDay = AssertSuccessful(restartedEngine.AssignNextDutyDay(restored));

        Assert.Equal(["C", "D"], nextDay);
        Assert.Equal(persistedOrder, restored.CurrentOrder);
        Assert.Equal(4, restored.CurrentOrderCursor);
    }

    [Fact]
    public void PendingConfigurationActivatesMidDayAndKeepsOldDailyCountForThatDay()
    {
        var engine = new DutyRosterEngine(new ScriptedShuffleSource());
        var state = Activate(engine, ["A", "B", "C", "D", "E"], dailyCount: 3);
        Assert.Equal(["A", "B", "C"], AssertSuccessful(engine.AssignNextDutyDay(state)));

        var pending = Configuration(["X", "Y", "Z", "W"], dailyCount: 2);
        Assert.Null(engine.ValidatePendingConfiguration(state, pending));
        state.PendingConfiguration = pending;

        var crossingDay = AssertSuccessful(engine.AssignNextDutyDay(state));

        Assert.Equal(["D", "E", "X"], crossingDay);
        Assert.Equal(3, crossingDay.Count);
        Assert.Equal(2, state.ActiveConfiguration!.DailyCount);
        Assert.Equal(["X", "Y", "Z", "W"], state.ActiveConfiguration.Names);
        Assert.Null(state.PendingConfiguration);
        Assert.Equal(1, state.CurrentOrderCursor);
    }

    [Fact]
    public void PendingConfigurationActivatesAtDayBoundaryAndUsesNewDailyCount()
    {
        var engine = new DutyRosterEngine(new ScriptedShuffleSource());
        var state = Activate(engine, ["A", "B", "C", "D"], dailyCount: 2);
        Assert.Equal(["A", "B"], AssertSuccessful(engine.AssignNextDutyDay(state)));

        var pending = Configuration(["X", "Y", "Z", "W"], dailyCount: 3);
        Assert.Null(engine.ValidatePendingConfiguration(state, pending));
        state.PendingConfiguration = pending;

        Assert.Equal(["C", "D"], AssertSuccessful(engine.AssignNextDutyDay(state)));
        Assert.NotNull(state.PendingConfiguration);
        Assert.Equal(2, state.ActiveConfiguration!.DailyCount);

        var firstDayOnNewConfiguration = AssertSuccessful(engine.AssignNextDutyDay(state));

        Assert.Equal(["X", "Y", "Z"], firstDayOnNewConfiguration);
        Assert.Equal(3, firstDayOnNewConfiguration.Count);
        Assert.Equal(3, state.ActiveConfiguration.DailyCount);
        Assert.Null(state.PendingConfiguration);
    }

    [Fact]
    public void ParseRosterTrimsBlankLinesAndReportsDuplicateNames()
    {
        var parsed = DutyRosterEngine.ParseRoster(" 张三 \r\n李四\n\n张三 ");

        Assert.Equal(["张三", "李四", "张三"], parsed.Names);
        Assert.Equal("张三", parsed.DuplicateName);
        Assert.NotNull(DutyRosterEngine.ValidateConfiguration(new DutyRosterConfiguration
        {
            Names = [.. parsed.Names],
            DailyCount = 1
        }));
    }

    [Fact]
    public void ImportTextPrefersNameColumnAndKeepsDuplicateForExistingValidation()
    {
        var imported = RosterImportService.ImportTextContent("班级\t学号\t学生姓名\n高一(1)班\t202601\t张三\n高一(1)班\t202602\t李四\n高一(1)班\t202603\t张三", "名单.tsv");

        Assert.Equal(["张三", "李四", "张三"], imported.Names);
        Assert.Contains("第 3 列", imported.Message);
        Assert.Contains("重复", imported.Message);
    }

    [Fact]
    public void ImportPlainTextHandlesNumberedAndSpaceSeparatedNames()
    {
        var imported = RosterImportService.ImportTextContent("1、张三\n2、李四\n王五 赵六", "名单.txt");

        Assert.Equal(["张三", "李四", "王五", "赵六"], imported.Names);
    }

    [Fact]
    public void ImportXlsxSelectsNameColumn()
    {
        var path = Path.Combine(Path.GetTempPath(), $"duty-roster-{Guid.NewGuid():N}.xlsx");
        try
        {
            CreateMinimalXlsx(path);

            var imported = RosterImportService.ImportFile(path);

            Assert.Equal(["王小明", "陈晨"], imported.Names);
            Assert.Contains("工作表“", imported.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void ValidateConfigurationRejectsDailyCountOutsideRosterBounds(int dailyCount)
    {
        var configuration = Configuration(["A", "B", "C"], dailyCount);

        Assert.NotNull(DutyRosterEngine.ValidateConfiguration(configuration));
    }

    [Fact]
    public void ValidatePendingConfigurationRejectsImpossibleCycleBoundaryFill()
    {
        var engine = new DutyRosterEngine(new ScriptedShuffleSource());
        var state = Activate(engine, ["A", "B", "C", "D", "E"], dailyCount: 3);
        Assert.Equal(["A", "B", "C"], AssertSuccessful(engine.AssignNextDutyDay(state)));
        var pending = Configuration(["D", "E"], dailyCount: 2);

        var error = engine.ValidatePendingConfiguration(state, pending);

        Assert.NotNull(error);
        Assert.Contains("补足 1 人", error);
        Assert.Contains("仅剩 0 人", error);
        Assert.Null(state.PendingConfiguration);
    }

    [Fact]
    public void EmptyRecoveredOrderIsTreatedAsCycleBoundaryForPendingConfiguration()
    {
        var engine = new DutyRosterEngine(new ScriptedShuffleSource());
        var state = Activate(engine, ["A", "B"], dailyCount: 1);
        state.PendingConfiguration = Configuration(["X", "Y", "Z"], dailyCount: 2);
        state.CurrentOrder = [];
        state.CurrentOrderCursor = 0;

        var assignment = AssertSuccessful(engine.AssignNextDutyDay(state));

        Assert.Equal(["X", "Y"], assignment);
        Assert.Equal(["X", "Y", "Z"], state.ActiveConfiguration!.Names);
        Assert.Null(state.PendingConfiguration);
    }

    [Fact]
    public void ExplicitNullCollectionsFromJsonAreRepairedBeforeNormalization()
    {
        var state = new DutyRosterPersistentState
        {
            CurrentOrder = [null!, "  A  ", ""],
            TodayAssignees = [" B ", null!],
            ActiveConfiguration = new DutyRosterConfiguration { Names = [null!, " C "] },
            PendingConfiguration = new DutyRosterConfiguration { Names = ["  D", " "] }
        };

        DutyRosterStateRepair.RepairDeserializedCollections(state);

        Assert.Equal(["A"], state.CurrentOrder);
        Assert.Equal(["B"], state.TodayAssignees);
        Assert.Equal(["C"], state.ActiveConfiguration.Names);
        Assert.Equal(["D"], state.PendingConfiguration.Names);
    }

    [Fact]
    public void ValidateConfigurationRejectsNullWhitespaceAndUntrimmedNames()
    {
        Assert.NotNull(DutyRosterEngine.ValidateConfiguration(Configuration([null!, "A"], 1)));
        Assert.NotNull(DutyRosterEngine.ValidateConfiguration(Configuration([" ", "A"], 1)));
        Assert.NotNull(DutyRosterEngine.ValidateConfiguration(Configuration([" A ", "B"], 1)));
    }

    private static DutyRosterPersistentState Activate(
        DutyRosterEngine engine,
        IReadOnlyList<string> names,
        int dailyCount)
    {
        var state = new DutyRosterPersistentState();
        engine.ActivateInitialConfiguration(state, Configuration(names, dailyCount));
        return state;
    }

    private static DutyRosterConfiguration Configuration(IReadOnlyList<string> names, int dailyCount) => new()
    {
        Names = [.. names],
        DailyCount = dailyCount
    };

    private static IReadOnlyList<string> AssertSuccessful(DutyDayAssignment assignment)
    {
        Assert.Null(assignment.Warning);
        return assignment.Names;
    }

    private static void CreateMinimalXlsx(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteZipEntry(archive, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            </Types>
            """);
        WriteZipEntry(archive, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        WriteZipEntry(archive, "xl/workbook.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets><sheet name="学生名单" sheetId="1" r:id="rId1"/></sheets>
            </workbook>
            """);
        WriteZipEntry(archive, "xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
            </Relationships>
            """);
        WriteZipEntry(archive, "xl/worksheets/sheet1.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <sheetData>
                <row r="1"><c r="A1" t="inlineStr"><is><t>学号</t></is></c><c r="B1" t="inlineStr"><is><t>姓名</t></is></c><c r="C1" t="inlineStr"><is><t>班级</t></is></c></row>
                <row r="2"><c r="A2"><v>202601</v></c><c r="B2" t="inlineStr"><is><t>王小明</t></is></c><c r="C2" t="inlineStr"><is><t>高一(1)班</t></is></c></row>
                <row r="3"><c r="A3"><v>202602</v></c><c r="B3" t="inlineStr"><is><t>陈晨</t></is></c><c r="C3" t="inlineStr"><is><t>高一(1)班</t></is></c></row>
              </sheetData>
            </worksheet>
            """);
    }

    private static void WriteZipEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private static void AssertCycle(IReadOnlyCollection<string> actual, IReadOnlyCollection<string> expected)
    {
        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(expected.Count, actual.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.True(expected.ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(actual));
    }

    private sealed class ScriptedShuffleSource(params int[] results) : IShuffleSource
    {
        private readonly Queue<int> _results = new(results);

        public int CallCount { get; private set; }

        public int Next(int maxExclusive)
        {
            CallCount++;
            var result = _results.Count > 0 ? _results.Dequeue() : maxExclusive - 1;
            if (result < 0 || result >= maxExclusive)
            {
                throw new InvalidOperationException(
                    $"Scripted shuffle result {result} is outside [0, {maxExclusive}).");
            }

            return result;
        }
    }

    private sealed class ThrowingShuffleSource : IShuffleSource
    {
        public int Next(int maxExclusive) =>
            throw new InvalidOperationException("A persisted in-progress cycle must not be reshuffled.");
    }
}
