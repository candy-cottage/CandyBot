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
/// 针对 <see cref="ForzaService.ApplyCachedTune"/> 的 MySQL 集成测试（Task 16）。
///
/// <para>每个用例均通过 <see cref="MySqlFixture.ResetDataAsync"/> 重置数据，
/// 利用独立 helper 灌入测试行，调用被测方法后以 probe helper 直接读表断言。</para>
/// </summary>
[Collection(MySqlCollection.Name)]
[Trait("Category", "MySql")]
public class ForzaApplyCachedTuneTests(MySqlFixture fixture)
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

    /// <summary>
    /// 直接用独立 helper 经 InsertTune 灌入一条调校行（含 identity/fingerprint）。
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

    /// <summary>
    /// 直接插入一条 VehicleTuneCache 行，模拟同步服务写入的缓存快照。
    /// VehicleId 由调用方提供（NOT NULL），SourceFileId/SourceIdentity 填占位值。
    /// </summary>
    private void SeedCacheRow(int tuneId, int vehicleId, string cls, string remarks, string? sourceFingerprint = "fp-cached")
    {
        using MySQLHelper sql = _fixture.CreateHelper();
        string fpExpr = sourceFingerprint == null ? "NULL" : $"'{sourceFingerprint}'";
        sql.Execute($@"
            INSERT INTO VehicleTuneCache
                (TuneId, VehicleId, Class, MatchType, Drivetrain, PerformancePreference,
                 TireCompound, SurfaceCondition, Strength, TuneRaw, Remarks,
                 LapTime, ExtData, SourceFileId, SourceIdentity, SourceFingerprint)
            VALUES
                ({tuneId}, {vehicleId}, '{cls}', '公路', '四驱', NULL,
                 NULL, NULL, NULL, NULL, '{remarks}',
                 NULL, NULL, 'test-file', 'test-identity', {fpExpr})");
        sql.Success.Should().BeTrue("SeedCacheRow 写入应成功");
    }

    /// <summary>读取一行调校（用独立 probe helper）。</summary>
    private DataRow ReadTune(int tuneId)
    {
        using MySQLHelper probe = _fixture.CreateHelper();
        DataRow? row = probe.ExecuteDataRow($"SELECT * FROM VehicleTunes WHERE Id = {tuneId}");
        row.Should().NotBeNull($"调校 {tuneId} 应存在");
        return row!;
    }

    private int CountCache(int tuneId)
    {
        using MySQLHelper probe = _fixture.CreateHelper();
        DataRow? row = probe.ExecuteDataRow($"SELECT COUNT(*) FROM VehicleTuneCache WHERE TuneId = {tuneId}");
        return Convert.ToInt32(row![0]);
    }

    // =========================================================================
    // 1. 应用缓存：清人手维护标记、状态回正常、删缓存行
    // =========================================================================
    [Fact]
    public async Task ApplyCachedTune_应用缓存_清人手维护_状态回正常_删缓存行()
    {
        await _fixture.ResetDataAsync();

        int vid = EnsureModel("ApplyMaker", "ModelApply1");
        VehicleTuneRequest seed = new()
        {
            Manufacturer = "ApplyMaker",
            Model = "ModelApply1",
            VehicleId = vid,
            Class = "S1",
            MatchType = "公路",
            Drivetrain = "四驱",
            TuneStatus = "有更新",
            IsManual = true,
            SourceFileId = "apply-file",
            LastUpdatedUserId = 1,
        };
        int tuneId = SeedTune(seed);

        // 标记为人手维护（IsManual=1）、有更新状态
        using (MySQLHelper raw = _fixture.CreateHelper())
        {
            raw.Execute($"UPDATE VehicleTunes SET IsManual = 1, TuneStatus = '有更新' WHERE Id = {tuneId}");
            raw.Success.Should().BeTrue();
        }

        // 灌入缓存行（新源：Class='A'，Remarks='cached remark'，SourceFingerprint 不同）
        SeedCacheRow(tuneId, vid, "A", "cached remark", "fp-new");

        // 执行
        using MySQLHelper applyHelper = _fixture.CreateHelper();
        ForzaService applySvc = new(NullLogger<ForzaService>.Instance, applyHelper, _forzaConfig);
        bool ok = applySvc.ApplyCachedTune(tuneId, actorUserId: 42, out string err);

        // 断言：操作成功
        ok.Should().BeTrue(err);
        err.Should().BeEmpty();

        // 断言：live 行已从缓存覆写
        DataRow live = ReadTune(tuneId);
        live["Class"].ToString().Should().Be("A", "Class 应从缓存覆写");
        live["Remarks"].ToString().Should().Be("cached remark", "Remarks 应从缓存覆写");
        live["SourceFingerprint"].ToString().Should().Be("fp-new", "SourceFingerprint 应从缓存更新");
        Convert.ToInt32(live["IsManual"]).Should().Be(0, "IsManual 应被清除为 0");
        live["TuneStatus"].ToString().Should().Be("正常", "TuneStatus 应重置为正常");
        Convert.ToInt64(live["LastUpdatedUserId"]).Should().Be(42, "应记录操作人 ID");

        // 断言：缓存行已删除
        CountCache(tuneId).Should().Be(0, "VehicleTuneCache 行应已删除");
    }

    // =========================================================================
    // 2. 无缓存行时返回 false
    // =========================================================================
    [Fact]
    public async Task ApplyCachedTune_无缓存行_返回false()
    {
        await _fixture.ResetDataAsync();

        int vid = EnsureModel("ApplyMaker", "ModelApply2");
        VehicleTuneRequest seed = new()
        {
            Manufacturer = "ApplyMaker",
            Model = "ModelApply2",
            VehicleId = vid,
            Class = "B",
            MatchType = "拉力",
            Drivetrain = "后驱",
            IsManual = true,
            SourceFileId = "apply-file-2",
            LastUpdatedUserId = 1,
        };
        int tuneId = SeedTune(seed);
        // 不插入缓存行

        using MySQLHelper applyHelper = _fixture.CreateHelper();
        ForzaService applySvc = new(NullLogger<ForzaService>.Instance, applyHelper, _forzaConfig);
        bool ok = applySvc.ApplyCachedTune(tuneId, actorUserId: 99, out string err);

        ok.Should().BeFalse("无缓存行时应返回 false");
        err.Should().NotBeEmpty("应给出错误描述");

        // live 行应未被修改
        DataRow live = ReadTune(tuneId);
        live["TuneStatus"].ToString().Should().Be("正常", "无缓存行时 live 状态不应变化");
    }
}
