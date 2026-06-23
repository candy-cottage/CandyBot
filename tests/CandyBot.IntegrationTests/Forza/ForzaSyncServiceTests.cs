using System.Data;
using CandyBot.IntegrationTests.Infrastructure;
using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Services;
using CandyCottage.CandyBot.Utilities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CandyBot.IntegrationTests.Forza;

/// <summary>
/// 针对 <see cref="ForzaSyncService.Reconcile"/> 的真实 MySQL 集成测试（Task 10）。
///
/// <para>这是整个 Forza 车辆表重构里最关键的正确性测试：协调引擎必须做到「按来源幂等」，
/// 既不能像旧的「清空+重灌」那样误删人手维护的数据，也不能在重复同步时撞唯一键或产生重复行。</para>
///
/// <para><b>测试约定：</b>每个用例先 <see cref="MySqlFixture.ResetDataAsync"/> 清空数据，
/// 通过 <see cref="EnsureModel"/> / <see cref="SeedTune"/> 灌入既有行，再调用
/// <see cref="ForzaSyncService.Reconcile"/>，最后用独立 probe helper 直接读表断言。
/// 由于 <see cref="MySQLHelper"/> 持有连接/事务状态，<b>每个操作都取一个全新 helper</b> 并自行 Dispose。</para>
/// </summary>
[Collection(MySqlCollection.Name)]
[Trait("Category", "MySql")]
public class ForzaSyncServiceTests(MySqlFixture fixture)
{
    private readonly MySqlFixture _fixture = fixture;
    private readonly ForzaConfig _forzaConfig = new();

    // ---- 辅助 ----

    /// <summary>用独立 helper upsert 一个车型，返回 VehicleId。</summary>
    private int EnsureModel(string manufacturer, string model, string? year = null)
    {
        using MySQLHelper sql = _fixture.CreateHelper();
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql, _forzaConfig);
        int id = svc.EnsureVehicleModel(
            new VehicleModelRequest { Manufacturer = manufacturer, Model = model, Year = year },
            0, out string err);
        err.Should().BeEmpty();
        id.Should().BeGreaterThan(0);
        return id;
    }

    /// <summary>构造一个解析调校请求（携带车型字段，供 Reconcile 在 Phase 1 解析 ModelKey）。</summary>
    private static VehicleTuneRequest ParsedTune(
        string manufacturer, string model,
        string cls = "S1", string matchType = "公路", string drivetrain = "四驱",
        string? perfPref = null, string? tireCompound = null,
        string? surfaceCondition = null, string? strength = null,
        string tuneRaw = "[111111111]", string? remarks = null, string? lapTime = null,
        string? year = null) => new()
        {
            Manufacturer = manufacturer,
            Model = model,
            Year = year,
            Class = cls,
            MatchType = matchType,
            Drivetrain = drivetrain,
            PerformancePreference = perfPref,
            TireCompound = tireCompound,
            SurfaceCondition = surfaceCondition,
            Strength = strength,
            TuneRaw = tuneRaw,
            Remarks = remarks,
            LapTime = lapTime,
        };

    /// <summary>
    /// 直接用独立 helper 经 InsertTune 灌入一条「同步管理」调校行（含 identity/fingerprint）。
    /// vehicleId 必须先经 <see cref="EnsureModel"/> 取得；req 须设好 SourceFileId 与车型字段。
    /// </summary>
    private int SeedTune(VehicleTuneRequest req, int seq = 0)
    {
        using MySQLHelper sql = _fixture.CreateHelper();
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql, _forzaConfig);
        sql.NewTransaction();
        int tuneId = svc.InsertTune(req, seq, out string err);
        sql.Commit();
        tuneId.Should().BeGreaterThan(0, err);
        return tuneId;
    }

    /// <summary>取一个全新 ForzaSyncService（用独立 helper）。</summary>
    private (ForzaSyncService sync, MySQLHelper sql) NewSync()
    {
        MySQLHelper sql = _fixture.CreateHelper();
        ForzaService forza = new(NullLogger<ForzaService>.Instance, sql, _forzaConfig);
        ForzaSyncService sync = new(NullLogger<ForzaSyncService>.Instance, sql, forza);
        return (sync, sql);
    }

    /// <summary>读取一行调校（用独立 probe helper）。</summary>
    private DataRow ReadTune(int tuneId)
    {
        using MySQLHelper probe = _fixture.CreateHelper();
        DataRow? row = probe.ExecuteDataRow($"SELECT * FROM VehicleTunes WHERE Id = {tuneId}");
        row.Should().NotBeNull($"调校 {tuneId} 应存在");
        return row!;
    }

    private int CountTunes(int vehicleId)
    {
        using MySQLHelper probe = _fixture.CreateHelper();
        DataRow? row = probe.ExecuteDataRow($"SELECT COUNT(*) FROM VehicleTunes WHERE VehicleId = {vehicleId}");
        return Convert.ToInt32(row![0]);
    }

    private int CountCache(int tuneId)
    {
        using MySQLHelper probe = _fixture.CreateHelper();
        DataRow? row = probe.ExecuteDataRow($"SELECT COUNT(*) FROM VehicleTuneCache WHERE TuneId = {tuneId}");
        return Convert.ToInt32(row![0]);
    }

    // =========================================================================
    // 1. 命中非人手维护 → 源变 → 就地更新、状态正常、用户为本次 actor
    // =========================================================================
    [Fact]
    public async Task 命中非人手维护_源变_就地更新_状态正常_用户记为actor()
    {
        await _fixture.ResetDataAsync();
        const string fileId = "file-1";
        int vid = EnsureModel("TestMaker", "ModelA");

        // 既有同步行（Remarks=老备注），identity 由 6 字段+modelKey+fileId 决定
        VehicleTuneRequest seed = ParsedTune("TestMaker", "ModelA", remarks: "老备注");
        seed.SourceFileId = fileId;
        seed.VehicleId = vid;
        int tuneId = SeedTune(seed);

        // 重跑：同一 identity（6 字段不变）但 Remarks 变 → 指纹变 → 就地更新
        VehicleTuneRequest parsed = ParsedTune("TestMaker", "ModelA", remarks: "新备注");
        var (sync, sql) = NewSync();
        ForzaSyncReport report;
        using (sql)
            report = sync.Reconcile([parsed], fileId, isCompleteSnapshot: true, actorUserId: 42);

        report.UpdatedInPlace.Should().Be(1);
        report.Inserted.Should().Be(0);
        CountTunes(vid).Should().Be(1, "就地更新不应新增行");

        DataRow row = ReadTune(tuneId);
        row["Remarks"].ToString().Should().Be("新备注", "显示列应被新源覆盖");
        row["TuneStatus"].ToString().Should().Be("正常");
        Convert.ToInt64(row["LastUpdatedUserId"]).Should().Be(42, "应记为本次 actor");
    }

    // =========================================================================
    // 2. 命中人手维护 + 指纹变 → 写缓存、置「有更新」、live 显示不被覆盖
    // =========================================================================
    [Fact]
    public async Task 命中人手维护_指纹变_写缓存_置有更新_live显示不被覆盖()
    {
        await _fixture.ResetDataAsync();
        const string fileId = "file-2";
        int vid = EnsureModel("TestMaker", "ModelB");

        VehicleTuneRequest seed = ParsedTune("TestMaker", "ModelB", remarks: "原始备注");
        seed.SourceFileId = fileId;
        seed.VehicleId = vid;
        int tuneId = SeedTune(seed);

        // 标记为人手维护
        using (MySQLHelper raw = _fixture.CreateHelper())
        {
            raw.Execute($"UPDATE VehicleTunes SET IsManual = 1 WHERE Id = {tuneId}");
            raw.Success.Should().BeTrue();
        }

        VehicleTuneRequest parsed = ParsedTune("TestMaker", "ModelB", remarks: "新源备注");
        var (sync, sql) = NewSync();
        ForzaSyncReport report;
        using (sql)
            report = sync.Reconcile([parsed], fileId, isCompleteSnapshot: true, actorUserId: 7);

        report.Cached.Should().Be(1);
        report.UpdatedInPlace.Should().Be(0);

        DataRow row = ReadTune(tuneId);
        row["TuneStatus"].ToString().Should().Be("有更新");
        row["Remarks"].ToString().Should().Be("原始备注", "人手维护行的 live 显示列不得被同步覆盖");

        CountCache(tuneId).Should().Be(1, "应写入缓存行捕获新源快照");
        using MySQLHelper probe = _fixture.CreateHelper();
        DataRow? cacheRow = probe.ExecuteDataRow($"SELECT Remarks FROM VehicleTuneCache WHERE TuneId = {tuneId}");
        cacheRow!["Remarks"].ToString().Should().Be("新源备注", "缓存应捕获新源快照");
    }

    // =========================================================================
    // 3. 命中人手维护 + 指纹不变 → 无操作（不刷「有更新」、不写缓存）
    // =========================================================================
    [Fact]
    public async Task 命中人手维护_指纹不变_无操作()
    {
        await _fixture.ResetDataAsync();
        const string fileId = "file-3";
        int vid = EnsureModel("TestMaker", "ModelC");

        VehicleTuneRequest seed = ParsedTune("TestMaker", "ModelC", remarks: "稳定备注");
        seed.SourceFileId = fileId;
        seed.VehicleId = vid;
        int tuneId = SeedTune(seed);

        using (MySQLHelper raw = _fixture.CreateHelper())
        {
            raw.Execute($"UPDATE VehicleTunes SET IsManual = 1 WHERE Id = {tuneId}");
            raw.Success.Should().BeTrue();
        }

        // 重跑：源完全相同 → 指纹不变 → 无操作
        VehicleTuneRequest parsed = ParsedTune("TestMaker", "ModelC", remarks: "稳定备注");
        var (sync, sql) = NewSync();
        ForzaSyncReport report;
        using (sql)
            report = sync.Reconcile([parsed], fileId, isCompleteSnapshot: true, actorUserId: 7);

        report.Cached.Should().Be(0);
        report.UpdatedInPlace.Should().Be(0);
        ReadTune(tuneId)["TuneStatus"].ToString().Should().Be("正常", "指纹不变不应置「有更新」");
        CountCache(tuneId).Should().Be(0, "指纹不变不应写缓存");
    }

    // =========================================================================
    // 4. 管理员改键后重跑同步仍命中（矩阵 #4 同步部分）：
    //    InsertTune→raw UPDATE 改显示值但冻结 identity+fingerprint →
    //    以原始源元组重跑 → 命中同一 TuneId（指纹未变→不变），无新增、无过时/删除
    // =========================================================================
    [Fact]
    public async Task 管理员改键后重跑同步仍命中无重复无误删()
    {
        await _fixture.ResetDataAsync();
        const string fileId = "file-4";
        int vid = EnsureModel("TestMaker", "ModelD");

        VehicleTuneRequest seed = ParsedTune("TestMaker", "ModelD", remarks: "源备注", drivetrain: "四驱");
        seed.SourceFileId = fileId;
        seed.VehicleId = vid;
        int tuneId = SeedTune(seed);

        // 模拟管理员编辑显示列，但 SourceIdentity / SourceFingerprint 被冻结（仅改 Drivetrain）
        using (MySQLHelper raw = _fixture.CreateHelper())
        {
            raw.Execute($"UPDATE VehicleTunes SET Drivetrain = '后驱' WHERE Id = {tuneId}");
            raw.Success.Should().BeTrue();
        }

        // 以「原始源元组」重跑：Reconcile 由 parsed 的源字段算出与既有冻结指纹相同的 fp → 命中、不变
        VehicleTuneRequest parsed = ParsedTune("TestMaker", "ModelD", remarks: "源备注", drivetrain: "四驱");
        var (sync, sql) = NewSync();
        ForzaSyncReport report;
        using (sql)
            report = sync.Reconcile([parsed], fileId, isCompleteSnapshot: true, actorUserId: 9);

        report.Inserted.Should().Be(0, "不应新增");
        report.Deleted.Should().Be(0, "不应删除");
        report.Expired.Should().Be(0, "不应过时");
        report.UpdatedInPlace.Should().Be(0, "指纹相同 → UNCHANGED，不写库");
        CountTunes(vid).Should().Be(1);
        ReadTune(tuneId)["Drivetrain"].ToString().Should().Be("后驱", "管理员改的显示值不被回退");
    }

    // =========================================================================
    // 5. 未命中 + 完整快照：非人手删除、人手置已过时
    // =========================================================================
    [Fact]
    public async Task 未命中_完整快照_非人手删除_人手置已过时()
    {
        await _fixture.ResetDataAsync();
        const string fileId = "file-5";
        int vid = EnsureModel("TestMaker", "ModelE");

        // 同步行（IsManual=0）
        VehicleTuneRequest s1 = ParsedTune("TestMaker", "ModelE", matchType: "公路", remarks: "auto");
        s1.SourceFileId = fileId; s1.VehicleId = vid;
        int autoId = SeedTune(s1);

        // 人手行（IsManual=1）——用不同 MatchType 确保两条 identity 独立
        VehicleTuneRequest s2 = ParsedTune("TestMaker", "ModelE", matchType: "拉力", remarks: "manual");
        s2.SourceFileId = fileId; s2.VehicleId = vid;
        int manualId = SeedTune(s2);
        using (MySQLHelper raw = _fixture.CreateHelper())
        {
            raw.Execute($"UPDATE VehicleTunes SET IsManual = 1 WHERE Id = {manualId}");
            raw.Success.Should().BeTrue();
        }

        // 完整快照里两条都缺席
        var (sync, sql) = NewSync();
        ForzaSyncReport report;
        using (sql)
            report = sync.Reconcile([], fileId, isCompleteSnapshot: true, actorUserId: 1);

        report.Deleted.Should().Be(1);
        report.Expired.Should().Be(1);

        using MySQLHelper probe = _fixture.CreateHelper();
        Convert.ToInt32(probe.ExecuteDataRow($"SELECT COUNT(*) FROM VehicleTunes WHERE Id = {autoId}")![0])
            .Should().Be(0, "非人手未命中行应被删除");
        ReadTune(manualId)["TuneStatus"].ToString().Should().Be("已过时", "人手未命中行应置已过时");
    }

    // =========================================================================
    // 6. 未命中 + 非完整快照：不删不过时
    // =========================================================================
    [Fact]
    public async Task 未命中_非完整快照_不删不过时()
    {
        await _fixture.ResetDataAsync();
        const string fileId = "file-6";
        int vid = EnsureModel("TestMaker", "ModelF");

        VehicleTuneRequest s1 = ParsedTune("TestMaker", "ModelF", matchType: "公路");
        s1.SourceFileId = fileId; s1.VehicleId = vid;
        int autoId = SeedTune(s1);

        VehicleTuneRequest s2 = ParsedTune("TestMaker", "ModelF", matchType: "拉力");
        s2.SourceFileId = fileId; s2.VehicleId = vid;
        int manualId = SeedTune(s2);
        using (MySQLHelper raw = _fixture.CreateHelper())
        {
            raw.Execute($"UPDATE VehicleTunes SET IsManual = 1 WHERE Id = {manualId}");
            raw.Success.Should().BeTrue();
        }

        var (sync, sql) = NewSync();
        ForzaSyncReport report;
        using (sql)
            report = sync.Reconcile([], fileId, isCompleteSnapshot: false, actorUserId: 1);

        report.Deleted.Should().Be(0);
        report.Expired.Should().Be(0);
        CountTunes(vid).Should().Be(2, "非完整快照不应删除任何行");
        ReadTune(autoId)["TuneStatus"].ToString().Should().Be("正常");
        ReadTune(manualId)["TuneStatus"].ToString().Should().Be("正常");
    }

    // =========================================================================
    // 7. 双来源隔离：同步 A 不动 B
    // =========================================================================
    [Fact]
    public async Task 双来源隔离_同步A不动B()
    {
        await _fixture.ResetDataAsync();
        int vid = EnsureModel("TestMaker", "ModelG");

        VehicleTuneRequest a = ParsedTune("TestMaker", "ModelG", remarks: "A原始");
        a.SourceFileId = "A"; a.VehicleId = vid;
        int aId = SeedTune(a);

        VehicleTuneRequest b = ParsedTune("TestMaker", "ModelG", remarks: "B原始");
        b.SourceFileId = "B"; b.VehicleId = vid;
        int bId = SeedTune(b);

        // 同步 A：改变 A 的源 + 完整快照（即便完整快照里没有 B 的身份，也不应触及 B）
        VehicleTuneRequest parsedA = ParsedTune("TestMaker", "ModelG", remarks: "A新源");
        var (sync, sql) = NewSync();
        using (sql)
            sync.Reconcile([parsedA], "A", isCompleteSnapshot: true, actorUserId: 1);

        ReadTune(aId)["Remarks"].ToString().Should().Be("A新源", "A 行应被更新");
        DataRow bRow = ReadTune(bId);
        bRow["Remarks"].ToString().Should().Be("B原始", "B 行不应被 A 的同步触及");
        bRow["TuneStatus"].ToString().Should().Be("正常", "B 行不应被置过时");
    }

    // =========================================================================
    // 8. 同身份组孪生：同车+6 字段同、仅干/湿不同 → 同 identity 不同 fp，
    //    两 parsed 各按指纹正确配对，无错配；改其中一条的非身份字段只更新该条
    // =========================================================================
    [Fact]
    public async Task 同身份组孪生_按指纹各自正确配对()
    {
        await _fixture.ResetDataAsync();
        const string fileId = "file-8";
        int vid = EnsureModel("TestMaker", "ModelH");

        // 两条孪生：6 个 identity 字段相同（含相同 TuneRaw），仅 SurfaceCondition（非 identity）不同
        VehicleTuneRequest dry = ParsedTune("TestMaker", "ModelH", surfaceCondition: "干", remarks: "干备注");
        dry.SourceFileId = fileId; dry.VehicleId = vid;
        int dryId = SeedTune(dry, seq: 0);

        VehicleTuneRequest wet = ParsedTune("TestMaker", "ModelH", surfaceCondition: "湿地", remarks: "湿备注");
        wet.SourceFileId = fileId; wet.VehicleId = vid;
        int wetId = SeedTune(wet, seq: 1);

        // 重跑：parsed 含两条，干不变 + 湿改备注 → 干指纹命中 dryId（UNCHANGED），湿指纹变 → 位置配对到 wetId
        VehicleTuneRequest pDry = ParsedTune("TestMaker", "ModelH", surfaceCondition: "干", remarks: "干备注");
        VehicleTuneRequest pWet = ParsedTune("TestMaker", "ModelH", surfaceCondition: "湿地", remarks: "湿新备注");

        var (sync, sql) = NewSync();
        ForzaSyncReport report;
        using (sql)
            report = sync.Reconcile([pDry, pWet], fileId, isCompleteSnapshot: true, actorUserId: 5);

        report.Inserted.Should().Be(0, "孪生应各自配对，不应有新增");
        report.Deleted.Should().Be(0, "无误删");
        report.UpdatedInPlace.Should().Be(1, "只有湿地一条指纹变 → 就地更新");
        CountTunes(vid).Should().Be(2);

        ReadTune(dryId)["Remarks"].ToString().Should().Be("干备注", "干孪生未变");
        ReadTune(wetId)["Remarks"].ToString().Should().Be("湿新备注", "只更新湿孪生");
    }

    // =========================================================================
    // 9. 同身份精确重复：DB 已有 seq0/1，parsed 仍两条 → 不撞唯一键
    // =========================================================================
    [Fact]
    public async Task 同身份精确重复_DB已有seq0和1_parsed两条_不撞唯一键()
    {
        await _fixture.ResetDataAsync();
        const string fileId = "file-9";
        int vid = EnsureModel("TestMaker", "ModelI");

        // 两条完全相同的源（同 identity 同 fp），seq 0/1
        VehicleTuneRequest dup0 = ParsedTune("TestMaker", "ModelI", remarks: "dup");
        dup0.SourceFileId = fileId; dup0.VehicleId = vid;
        int id0 = SeedTune(dup0, seq: 0);

        VehicleTuneRequest dup1 = ParsedTune("TestMaker", "ModelI", remarks: "dup");
        dup1.SourceFileId = fileId; dup1.VehicleId = vid;
        int id1 = SeedTune(dup1, seq: 1);

        // 重跑两条相同源 → 两条指纹各命中一条既有行（fp 相同），无新增、不撞 uq_source_identity
        VehicleTuneRequest p0 = ParsedTune("TestMaker", "ModelI", remarks: "dup");
        VehicleTuneRequest p1 = ParsedTune("TestMaker", "ModelI", remarks: "dup");

        var (sync, sql) = NewSync();
        ForzaSyncReport report;
        using (sql)
            report = sync.Reconcile([p0, p1], fileId, isCompleteSnapshot: true, actorUserId: 1);

        report.ErrorMessage.Should().BeNull("不应因唯一键冲突报错");
        report.Inserted.Should().Be(0, "两条都应命中既有行，不新增");
        CountTunes(vid).Should().Be(2, "仍是两行，未因唯一键冲突而失败或重复");
        _ = id0; _ = id1;
    }

    // =========================================================================
    // 10. 哨兵 migration 行不参与协调
    // =========================================================================
    [Fact]
    public async Task 哨兵migration行不参与协调()
    {
        await _fixture.ResetDataAsync();
        const string fileId = "file-10";
        int vid = EnsureModel("TestMaker", "ModelJ");

        // 哨兵行：SourceFileId='migration', SourceIdentity=''
        int sentinelId;
        using (MySQLHelper raw = _fixture.CreateHelper())
        {
            raw.Execute($@"INSERT INTO VehicleTunes
                (VehicleId, Class, MatchType, SourceFileId, SourceIdentity, SourceIdentitySeq, IsManual, TuneStatus)
                VALUES ({vid}, 'S1', '公路', 'migration', '', 0, 0, '正常')");
            raw.Success.Should().BeTrue();
            sentinelId = (int)raw.LastInsertId;
        }

        // 对真实 fileId 做完整快照、parsed 为空：只应影响该 fileId 的行，不触及 migration 哨兵
        var (sync, sql) = NewSync();
        ForzaSyncReport report;
        using (sql)
            report = sync.Reconcile([], fileId, isCompleteSnapshot: true, actorUserId: 1);

        report.Deleted.Should().Be(0);
        report.Expired.Should().Be(0);
        using MySQLHelper probe = _fixture.CreateHelper();
        Convert.ToInt32(probe.ExecuteDataRow($"SELECT COUNT(*) FROM VehicleTunes WHERE Id = {sentinelId}")![0])
            .Should().Be(1, "migration 哨兵行不应被协调删除");
        ReadTune(sentinelId)["TuneStatus"].ToString().Should().Be("正常", "哨兵行不应被置过时");
    }
}
