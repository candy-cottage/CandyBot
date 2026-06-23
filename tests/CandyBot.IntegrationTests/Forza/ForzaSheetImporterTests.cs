using System.Data;
using System.Text.Json;
using CandyBot.IntegrationTests.Infrastructure;
using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Services;
using CandyCottage.CandyBot.Utilities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CandyBot.IntegrationTests.Forza;

[Collection(MySqlCollection.Name)]
[Trait("Category", "MySql")]
public class ForzaSheetImporterTests(MySqlFixture fixture)
{
    private readonly MySqlFixture _fixture = fixture;
    private readonly ForzaConfig _forzaConfig = new();

    // ---- 夹具与辅助 ----

    private static Dictionary<string, List<List<string>>> LoadFixture()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Forza", "DSXRieHNsZXpFZmZ4.json");
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<string, List<List<string>>>>(json)!;
    }

    /// <summary>构造一个使用真实 ForzaSyncService 的导入器（每次取全新 helper）。</summary>
    private (ForzaSheetImporter importer, MySQLHelper sql) NewImporter()
    {
        MySQLHelper sql = _fixture.CreateHelper();
        ForzaService forzaService = new(NullLogger<ForzaService>.Instance, sql, _forzaConfig);
        ForzaSyncService syncService = new(NullLogger<ForzaSyncService>.Instance, sql, forzaService);
        ForzaSheetImporter importer = new(
            NullLogger<ForzaSheetImporter>.Instance,
            sheetService: null!,
            sql,
            syncService,
            new NoOpSheetParseDiagnostic());
        return (importer, sql);
    }

    private int CountRows(string table)
    {
        using MySQLHelper probe = _fixture.CreateHelper();
        DataRow? row = probe.ExecuteDataRow($"SELECT COUNT(*) FROM `{table}`");
        return Convert.ToInt32(row![0]);
    }

    private int CountRowsWhere(string table, string whereClause)
    {
        using MySQLHelper probe = _fixture.CreateHelper();
        DataRow? row = probe.ExecuteDataRow($"SELECT COUNT(*) FROM `{table}` {whereClause}");
        return Convert.ToInt32(row![0]);
    }

    // =========================================================================
    // 1. 完整真实 JSON → 首次导入调校、车型去重、重跑幂等
    // =========================================================================
    [Fact]
    public async Task 完整真实Json_首次导入调校_车型去重_重跑幂等()
    {
        await _fixture.ResetDataAsync();
        const string fileId = "DSXRieHNsZXpFZmZ4";
        Dictionary<string, List<List<string>>> sheets = LoadFixture();

        // 第一次导入
        var (importer1, sql1) = NewImporter();
        ForzaSheetSyncReport first;
        using (sql1) first = await importer1.SyncLoadedSheetsAsync(sheets, fileId);

        first.Success.Should().BeTrue(first.ErrorMessage);
        first.InsertedTuneCount.Should().BeGreaterThan(0, "首次导入应有新增调校行");

        int tuneCount = CountRows("VehicleTunes");
        int vehicleCount = CountRows("Vehicles");

        tuneCount.Should().Be(first.InsertedTuneCount, "调校行数应与 InsertedTuneCount 一致");
        vehicleCount.Should().BeLessThan(tuneCount, "车型数（去重）应少于调校行数");
        vehicleCount.Should().BeGreaterThan(0, "应有车型");

        // 第二次导入（幂等：同 fileId 完整快照）
        var (importer2, sql2) = NewImporter();
        ForzaSheetSyncReport second;
        using (sql2) second = await importer2.SyncLoadedSheetsAsync(sheets, fileId);

        second.Success.Should().BeTrue(second.ErrorMessage);
        second.InsertedTuneCount.Should().Be(0, "幂等重跑：无新增行");
        second.DeletedTuneCount.Should().Be(0, "幂等重跑：无删除行");

        CountRows("VehicleTunes").Should().Be(tuneCount, "重跑后调校行数不变");
        CountRows("Vehicles").Should().Be(vehicleCount, "重跑后车型数不变");
    }

    // =========================================================================
    // 2. 两来源隔离：先 A 后 B，A 调校仍在
    // =========================================================================
    [Fact]
    public async Task 两来源隔离_先A后B_A调校仍在()
    {
        await _fixture.ResetDataAsync();

        // 构造最小合法表格 A（1 条调校）
        Dictionary<string, List<List<string>>> sheetsA = BuildMinimalSheets("AModel", "AMaker", "S1-公路");
        // 构造最小合法表格 B（1 条不同调校），完整快照
        Dictionary<string, List<List<string>>> sheetsB = BuildMinimalSheets("BModel", "BMaker", "S1-公路");

        // 导入 A
        var (importerA, sqlA) = NewImporter();
        ForzaSheetSyncReport reportA;
        using (sqlA) reportA = await importerA.SyncLoadedSheetsAsync(sheetsA, "fileId-A");

        reportA.Success.Should().BeTrue(reportA.ErrorMessage);
        int tunesAfterA = CountRows("VehicleTunes");
        tunesAfterA.Should().Be(1, "A 导入后应有 1 条调校");

        // 导入 B（完整快照，fileId 不同）
        var (importerB, sqlB) = NewImporter();
        ForzaSheetSyncReport reportB;
        using (sqlB) reportB = await importerB.SyncLoadedSheetsAsync(sheetsB, "fileId-B");

        reportB.Success.Should().BeTrue(reportB.ErrorMessage);

        // A 的调校行不受 B 的完整快照影响（来源隔离）
        int tunesAfterB = CountRows("VehicleTunes");
        tunesAfterB.Should().Be(2, "B 导入后 A+B 共 2 条调校行");

        // A 的调校行仍存在
        int aStillExists = CountRowsWhere("VehicleTunes", "WHERE SourceFileId = 'fileId-A'");
        aStillExists.Should().Be(1, "来源 A 的调校行在导入 B 后仍存在");
    }

    // =========================================================================
    // 3. 部分解析安全：跳过一张班级 sheet → 该 sheet 调校不被过时或删除
    // =========================================================================
    [Fact]
    public async Task 部分解析安全_跳过一张班级sheet_该sheet调校不被过时或删除()
    {
        await _fixture.ResetDataAsync();
        const string fileId = "file-partial";

        // 完整快照：两张班级 sheet
        Dictionary<string, List<List<string>>> fullSheets = new()
        {
            ["S1-公路"] = MakeSheetGrid("MakerX", "ModelX", "S1", "公路"),
            ["S2-公路"] = MakeSheetGrid("MakerY", "ModelY", "S2", "公路"),
        };

        var (importer1, sql1) = NewImporter();
        ForzaSheetSyncReport full;
        using (sql1) full = await importer1.SyncLoadedSheetsAsync(fullSheets, fileId);
        full.Success.Should().BeTrue(full.ErrorMessage);

        int tunesAfterFull = CountRows("VehicleTunes");
        tunesAfterFull.Should().Be(2, "完整快照后应有 2 条调校");

        // 部分快照：S1-公路 正常；S2-公路 有内容但无有效表头 → 被加入 skippedClassSheets → isCompleteSnapshot=false
        Dictionary<string, List<List<string>>> partialSheets = new()
        {
            ["S1-公路"] = MakeSheetGrid("MakerX", "ModelX", "S1", "公路"),
            // S2-公路 存在于本次表格集合中，但只有无效内容（无「制造商」+「型号」表头对）
            // → VehicleSheetParser 找不到 header → 加入 skippedClassSheets → complete=false
            ["S2-公路"] = [["这是注释行"], ["无有效表头"]],
        };

        var (importer2, sql2) = NewImporter();
        ForzaSheetSyncReport partial;
        using (sql2) partial = await importer2.SyncLoadedSheetsAsync(partialSheets, fileId);
        partial.Success.Should().BeTrue(partial.ErrorMessage);

        // S2-公路 的调校行不被过时或删除（非完整快照不触发 missing 处理）
        int s2TunesStillExist = CountRowsWhere("VehicleTunes", "WHERE SourceSheet = 'S2-公路'");
        s2TunesStillExist.Should().Be(1, "部分快照下 S2-公路 的调校行不应被删除/过时");

        // S2-公路 的调校行状态应仍为正常（未被置为「已过时」）
        int s2ExpiredCount = CountRowsWhere("VehicleTunes",
            "WHERE SourceSheet = 'S2-公路' AND TuneStatus = '已过时'");
        s2ExpiredCount.Should().Be(0, "部分快照下 S2-公路 的调校行不应被置为已过时");
    }

    // =========================================================================
    // 4. 解析零行 → 拒绝覆盖，车型与调校均不变
    // =========================================================================
    [Fact]
    public async Task 解析零行_拒绝覆盖_车型与调校均不变()
    {
        await _fixture.ResetDataAsync();
        const string fileId = "file-zero";

        // 先灌入 1 辆车的调校
        Dictionary<string, List<List<string>>> seedSheets = new()
        {
            ["S1-公路"] = MakeSheetGrid("SeedMaker", "SeedModel", "S1", "公路"),
        };

        var (seeder, sqlSeed) = NewImporter();
        ForzaSheetSyncReport seed;
        using (sqlSeed) seed = await seeder.SyncLoadedSheetsAsync(seedSheets, fileId);
        seed.Success.Should().BeTrue(seed.ErrorMessage);

        int vehiclesBefore = CountRows("Vehicles");
        int tunesBefore = CountRows("VehicleTunes");
        vehiclesBefore.Should().Be(1);
        tunesBefore.Should().Be(1);

        // 空表格导入 → 应拒绝
        var (importer, sql) = NewImporter();
        ForzaSheetSyncReport report;
        using (sql) report = await importer.SyncLoadedSheetsAsync([], fileId);

        report.Success.Should().BeFalse("解析零行时应拒绝覆盖");
        report.ErrorMessage.Should().Contain("解析结果为 0");

        // 车型表与调校表均不变
        CountRows("Vehicles").Should().Be(vehiclesBefore, "车型表不应被改动");
        CountRows("VehicleTunes").Should().Be(tunesBefore, "调校表不应被改动");
    }

    // =========================================================================
    // 构建测试用最小合法表格数据
    // =========================================================================

    /// <summary>构造包含一张名为 <paramref name="sheetName"/> 的班级表格，含一辆车的调校。</summary>
    private static Dictionary<string, List<List<string>>> BuildMinimalSheets(
        string model, string manufacturer, string sheetName)
        => new() { [sheetName] = MakeSheetGrid(manufacturer, model,
            ExtractClass(sheetName), ExtractMatchType(sheetName)) };

    private static string ExtractClass(string sheetName)
        => sheetName.StartsWith("S2-", StringComparison.OrdinalIgnoreCase) ? "S2" :
           sheetName.StartsWith("S1-", StringComparison.OrdinalIgnoreCase) ? "S1" :
           sheetName.StartsWith("A-", StringComparison.OrdinalIgnoreCase) ? "A" :
           sheetName.StartsWith("B-", StringComparison.OrdinalIgnoreCase) ? "B" :
           sheetName.StartsWith("C-", StringComparison.OrdinalIgnoreCase) ? "C" : "R";

    private static string ExtractMatchType(string sheetName)
        => sheetName.Contains("公路", StringComparison.Ordinal) ? "公路" : "拉力";

    /// <summary>
    /// 生成两行数据（表头 + 1 条记录）的 grid，VehicleSheetParser 可以解析出 1 辆车。
    /// </summary>
    private static List<List<string>> MakeSheetGrid(
        string manufacturer, string model, string cls, string matchType)
        =>
        [
            // 表头行：VehicleSheetParser 检查 "制造商" + "型号" 是否同时存在
            ["制造商", "型号", "年份", "传动", "属性", "胎面", "干/湿", "强度", "调校", "备注", "圈速测试"],
            // 数据行
            [manufacturer, model, "2023", "四驱", "均衡", "运动", "", "", "[111111111]", "", ""],
        ];
}
