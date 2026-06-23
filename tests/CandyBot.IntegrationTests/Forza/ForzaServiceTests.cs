using System.Data;
using CandyBot.IntegrationTests.Infrastructure;
using CandyCottage.CandyBot.Controllers;
using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Services;
using CandyCottage.CandyBot.Utilities;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CandyBot.IntegrationTests.Forza;

/// <summary>
/// 针对 <see cref="ForzaService"/> 的真实 MySQL 集成测试（B09）。
///
/// <para><b>关键事实（来自阅读 Services/ForzaService.cs 与 Controllers/ForzaController.cs）：</b></para>
/// <list type="bullet">
///   <item><b>AddVehicle 必填字段</b>：Manufacturer、Model、Class、MatchType、Drivetrain
///   缺任意一个都会返回 false + 对应中文错误消息，不触碰数据库。Year 等其他字段可选。</item>
///   <item><b>GetVehicleById 的 @id bug</b>：原 ForzaController 直接调用
///   <c>GetVehicleDataTable("WHERE Id = @id", 1, 1)</c>，但从未绑定 @id 参数，
///   导致查询始终失败。修复方法：新增 <c>ForzaService.GetVehicleById(int id)</c>，
///   在内部先 <c>sql.Parameters["@id"] = id</c> 再委托给 GetVehicleDataTable。
///   控制器改调新方法。</item>
///   <item><b>EscapeLikeWildcards</b> 是 private 方法，通过 <c>GetVehicleInfoRaw</c> 间接测试。
///   MySQL 的 LIKE 默认以 <c>\</c> 作为转义符，因此 <c>\\%</c> 等同于字面量 <c>%</c>。</item>
///   <item><b>OR vs AND</b>：<c>GetVehiclesByClassOrMatchType</c> 用 OR 拼接条件；
///   <c>GetVehiclesByClassAndMatchType</c> 用 AND 拼接条件。两者在某些种子数据上结果集大小必然不同。</item>
///   <item><b>DeleteVehiclesByIds</b> 返回的 deletedCount 是 ids.Count（调用前去重的 ID 列表长度），
///   而非实际影响行数——即便 ID 不存在，只要语句执行成功仍返回 ids.Count。
///   因此"删除不存在 ID"测试只验证行不存在，而非依赖 deletedCount=0。</item>
/// </list>
/// </summary>
[Collection(MySqlCollection.Name)]
[Trait("Category", "MySql")]
public class ForzaServiceTests(MySqlFixture fixture)
{
    private readonly MySqlFixture _fixture = fixture;
    private readonly ForzaConfig _forzaConfig = new();

    // ---- 种子辅助 ----

    /// <summary>创建一个最小合法 VehicleRequest，可按需覆写字段。</summary>
    private static VehicleRequest MinimalVehicle(
        string manufacturer = "TestMaker",
        string model = "TestModel",
        string cls = "S1",
        string matchType = "Road",
        string drivetrain = "AWD") => new()
        {
            Manufacturer = manufacturer,
            Model = model,
            Class = cls,
            MatchType = matchType,
            Drivetrain = drivetrain,
        };

    /// <summary>
    /// 插入一辆车并返回其自增 Id。使用专属 helper 避免与测试 helper 的参数/事务状态纠缠。
    /// </summary>
    private int SeedVehicle(VehicleRequest req)
    {
        using MySQLHelper sql = _fixture.CreateHelper();
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql, _forzaConfig);
        svc.AddVehicle(req, out string err).Should().BeTrue(err);
        return (int)sql.LastInsertId;
    }

    // =========================================================================
    // AddVehicle 校验（必填字段 + 成功路径）
    // =========================================================================

    [Fact]
    public async Task AddVehicle_缺少Manufacturer返回false且错误消息正确()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql, _forzaConfig);

        VehicleRequest req = MinimalVehicle();
        req.Manufacturer = null;

        bool ok = svc.AddVehicle(req, out string error);

        ok.Should().BeFalse();
        error.Should().Be("制造商不能为空。");
    }

    [Fact]
    public async Task AddVehicle_缺少Model返回false且错误消息正确()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql, _forzaConfig);

        VehicleRequest req = MinimalVehicle();
        req.Model = null;

        bool ok = svc.AddVehicle(req, out string error);

        ok.Should().BeFalse();
        error.Should().Be("型号不能为空。");
    }

    [Fact]
    public async Task AddVehicle_缺少Class返回false且错误消息正确()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql, _forzaConfig);

        VehicleRequest req = MinimalVehicle();
        req.Class = null;

        bool ok = svc.AddVehicle(req, out string error);

        ok.Should().BeFalse();
        error.Should().Be("等级不能为空。");
    }

    [Fact]
    public async Task AddVehicle_缺少MatchType返回false且错误消息正确()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql, _forzaConfig);

        VehicleRequest req = MinimalVehicle();
        req.MatchType = null;

        bool ok = svc.AddVehicle(req, out string error);

        ok.Should().BeFalse();
        error.Should().Be("比赛类型不能为空。");
    }

    [Fact]
    public async Task AddVehicle_缺少Drivetrain返回false且错误消息正确()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql, _forzaConfig);

        VehicleRequest req = MinimalVehicle();
        req.Drivetrain = null;

        bool ok = svc.AddVehicle(req, out string error);

        ok.Should().BeFalse();
        error.Should().Be("驱动类型不能为空。");
    }

    [Fact]
    public async Task AddVehicle_完整必填字段新增成功且可查询到()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql, _forzaConfig);

        VehicleRequest req = MinimalVehicle("Honda", "Civic", "A", "Road", "FWD");
        req.Year = "2022";

        bool ok = svc.AddVehicle(req, out string error);
        int newId = (int)sql.LastInsertId;

        ok.Should().BeTrue(error);
        newId.Should().BeGreaterThan(0);

        // 用 GetVehicleInfoRaw 验证车辆可查询到
        using MySQLHelper probe = _fixture.CreateHelper();
        ForzaService probeSvc = new(NullLogger<ForzaService>.Instance, probe, _forzaConfig);
        var (success, _, data, _) = probeSvc.GetVehicleInfoRaw("Civic", 1);
        success.Should().BeTrue();
        data.Should().NotBeNull();
        data!.Rows.Count.Should().BeGreaterThan(0);
        data.Rows[0]["Model"].ToString().Should().Be("Civic");
    }

    [Fact]
    public async Task InsertVehicle_不开启自身事务_可由外层事务回滚()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql, _forzaConfig);

        sql.NewTransaction();
        bool ok = svc.InsertVehicle(MinimalVehicle("RollbackMaker", "RollbackModel", "A", "公路", "四驱"), out string error);
        ok.Should().BeTrue(error);
        sql.Rollback();

        using MySQLHelper probe = _fixture.CreateHelper();
        DataRow? row = probe.ExecuteDataRow("SELECT COUNT(*) FROM Vehicles WHERE Manufacturer='RollbackMaker'");
        Convert.ToInt32(row![0]).Should().Be(0);
    }

    // =========================================================================
    // UpdateVehicle
    // =========================================================================

    [Fact]
    public async Task UpdateVehicle_修改字段后可查询到新值()
    {
        await _fixture.ResetDataAsync();
        int id = SeedVehicle(MinimalVehicle("Toyota", "Supra", "S1", "Road", "RWD"));

        using MySQLHelper sql = _fixture.CreateHelper();
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql, _forzaConfig);
        VehicleRequest upd = new() { Id = id, Remarks = "极好操控" };
        bool ok = svc.UpdateVehicle(upd, out string error);

        ok.Should().BeTrue(error);

        // 查询验证 Remarks 已更新
        using MySQLHelper probe = _fixture.CreateHelper();
        ForzaService probeSvc = new(NullLogger<ForzaService>.Instance, probe, _forzaConfig);
        var (success, _, data, _) = probeSvc.GetVehicleById(id);
        success.Should().BeTrue();
        data!.Rows[0]["Remarks"].ToString().Should().Be("极好操控");
    }

    // =========================================================================
    // DeleteVehiclesByIds
    // =========================================================================

    [Fact]
    public async Task DeleteVehiclesByIds_删除存在的车辆后查不到该车()
    {
        await _fixture.ResetDataAsync();
        int id = SeedVehicle(MinimalVehicle("BMW", "M3", "S1", "Road", "RWD"));

        using MySQLHelper sql = _fixture.CreateHelper();
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql, _forzaConfig);
        var (success, _, deletedCount) = svc.DeleteVehiclesByIds([id]);

        success.Should().BeTrue();
        deletedCount.Should().Be(1);

        // 确认该行已不存在
        using MySQLHelper probe = _fixture.CreateHelper();
        ForzaService probeSvc = new(NullLogger<ForzaService>.Instance, probe, _forzaConfig);
        var (s2, _, data, _) = probeSvc.GetVehicleById(id);
        // success=true 但 data 为空（查询本身成功，但无行）
        (s2 == false || data == null || data.Rows.Count == 0).Should().BeTrue("已删除的车辆不应再被查到");
    }

    [Fact]
    public async Task DeleteVehiclesByIds_空列表返回false()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql, _forzaConfig);

        var (success, message, _) = svc.DeleteVehiclesByIds([]);

        success.Should().BeFalse();
        message.Should().Be("未提供有效的车辆ID");
    }

    // =========================================================================
    // GetVehicleInfoRaw — 名称搜索
    // =========================================================================

    [Fact]
    public async Task GetVehicleInfoRaw_按制造商关键字命中车辆()
    {
        await _fixture.ResetDataAsync();
        SeedVehicle(MinimalVehicle("Porsche", "911 GT3", "S2", "Road", "RWD"));
        SeedVehicle(MinimalVehicle("Ferrari", "488", "S2", "Road", "RWD"));

        using MySQLHelper sql = _fixture.CreateHelper();
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql, _forzaConfig);

        var (success, _, data, _) = svc.GetVehicleInfoRaw("Porsche", 1);

        success.Should().BeTrue();
        data.Should().NotBeNull();
        data!.Rows.Count.Should().Be(1);
        data.Rows[0]["Manufacturer"].ToString().Should().Be("Porsche");
    }

    [Fact]
    public async Task GetVehicleInfoRaw_无匹配关键字返回空()
    {
        await _fixture.ResetDataAsync();
        SeedVehicle(MinimalVehicle("Subaru", "WRX", "A", "Rally", "AWD"));

        using MySQLHelper sql = _fixture.CreateHelper();
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql, _forzaConfig);

        var (success, _, data, totalPages) = svc.GetVehicleInfoRaw("NotExistCar", 1);

        // 查询本身成功，但结果集为空（totalPages=0 或 data 为空行）
        (success == false || data == null || data.Rows.Count == 0 || totalPages == 0).Should().BeTrue("不存在的关键字应无命中");
    }

    // =========================================================================
    // 组合名搜索 — “制造商简称+型号”紧凑匹配（如 查车 法拉利FXX）
    // 真实数据制造商形如 "Ferrari 法拉利"，用户输入“法拉利FXX”不含完整制造商串，
    // 单列 LIKE 无法命中，需要“制造商+型号去空格”的紧凑组合条件。
    // =========================================================================

    [Fact]
    public async Task GetVehicleInfoRaw_组合名称无空格命中制造商和型号()
    {
        await _fixture.ResetDataAsync();
        SeedVehicle(MinimalVehicle("Ferrari 法拉利", "FXX", "S2", "Road", "AWD"));
        SeedVehicle(MinimalVehicle("Porsche", "911 GT3", "S2", "Road", "RWD"));

        using MySQLHelper sql = _fixture.CreateHelper();
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql, _forzaConfig);

        var (success, _, data, _) = svc.GetVehicleInfoRaw("法拉利FXX", 1);

        success.Should().BeTrue();
        data.Should().NotBeNull();
        data!.Rows.Count.Should().Be(1, "“法拉利FXX”应命中 Manufacturer=\"Ferrari 法拉利\" + Model=\"FXX\"，且不误中 Porsche");
        data.Rows[0]["Model"].ToString().Should().Be("FXX");
    }

    [Fact]
    public async Task GetVehicleInfoRaw_组合名称有空格命中制造商和型号()
    {
        await _fixture.ResetDataAsync();
        SeedVehicle(MinimalVehicle("Ferrari 法拉利", "FXX", "S2", "Road", "AWD"));
        SeedVehicle(MinimalVehicle("Porsche", "911 GT3", "S2", "Road", "RWD"));

        using MySQLHelper sql = _fixture.CreateHelper();
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql, _forzaConfig);

        var (success, _, data, _) = svc.GetVehicleInfoRaw("法拉利 FXX", 1);

        success.Should().BeTrue();
        data.Should().NotBeNull();
        data!.Rows.Count.Should().Be(1, "“法拉利 FXX”去空格后应命中 Manufacturer=\"Ferrari 法拉利\" + Model=\"FXX\"");
        data.Rows[0]["Model"].ToString().Should().Be("FXX");
    }

    [Fact]
    public async Task GetVehicleInfoRaw_不存在组合名称不误命中()
    {
        await _fixture.ResetDataAsync();
        SeedVehicle(MinimalVehicle("Ferrari 法拉利", "FXX", "S2", "Road", "AWD"));
        SeedVehicle(MinimalVehicle("Porsche", "911 GT3", "S2", "Road", "RWD"));

        using MySQLHelper sql = _fixture.CreateHelper();
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql, _forzaConfig);

        // “法拉利”与“911”分别存在于两辆不同的车，但组合名“法拉利911”不存在——不应跨行拼接误命中
        var (success, _, data, totalPages) = svc.GetVehicleInfoRaw("法拉利911", 1);

        (success == false || data == null || data.Rows.Count == 0 || totalPages == 0).Should().BeTrue("组合搜索不应跨车辆拼接误命中");
    }

    [Fact]
    public async Task GetVehicleInfo_OR回退路径支持紧凑组合名称()
    {
        await _fixture.ResetDataAsync();
        SeedVehicle(MinimalVehicle("Ferrari 法拉利", "FXX", "S2", "Road", "AWD"));

        using MySQLHelper sql = _fixture.CreateHelper();
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql, _forzaConfig);

        // 模拟聊天指令 ParseManufacturerModel 解析失败后的真实回退：manufacturer == model == 原始输入，useOr=true
        var (success, msg) = svc.GetVehicleInfo("法拉利FXX", "法拉利FXX", true, 1, out int totalPages);

        success.Should().BeTrue();
        msg.Should().Contain("Ferrari 法拉利 FXX");
        totalPages.Should().Be(1);
    }

    // =========================================================================
    // 组合名搜索 — 制造商英文别名（如 FerrariFXX：“法拉利”隔在 Ferrari 和 FXX 中间，
    // 紧凑串 "Ferrari法拉利FXX" 不含 "FerrariFXX"，需要服务层别名拆分）
    // =========================================================================

    [Fact]
    public async Task GetVehicleInfoRaw_英文制造商别名组合命中车辆()
    {
        await _fixture.ResetDataAsync();
        SeedVehicle(MinimalVehicle("Ferrari 法拉利", "FXX", "S2", "Road", "AWD"));
        SeedVehicle(MinimalVehicle("Porsche", "911 GT3", "S2", "Road", "RWD"));

        using MySQLHelper sql = _fixture.CreateHelper();
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql, _forzaConfig);

        var (success, _, data, _) = svc.GetVehicleInfoRaw("FerrariFXX", 1);

        success.Should().BeTrue();
        data.Should().NotBeNull();
        data!.Rows.Count.Should().Be(1);
        data.Rows[0]["Manufacturer"].ToString().Should().Be("Ferrari 法拉利");
        data.Rows[0]["Model"].ToString().Should().Be("FXX");
    }

    [Fact]
    public async Task GetVehicleInfoRaw_多词英文制造商别名组合命中车辆()
    {
        await _fixture.ResetDataAsync();
        SeedVehicle(MinimalVehicle("Aston Martin 阿斯顿·马丁", "Valkyrie", "S2", "Road", "AWD"));

        using MySQLHelper sql = _fixture.CreateHelper();
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql, _forzaConfig);

        var (success, _, data, _) = svc.GetVehicleInfoRaw("AstonMartinValkyrie", 1);

        success.Should().BeTrue();
        data.Should().NotBeNull();
        data!.Rows.Count.Should().Be(1);
        data.Rows[0]["Model"].ToString().Should().Be("Valkyrie");
    }

    [Fact]
    public async Task GetVehicleInfoRaw_英文别名组合不跨车辆误命中()
    {
        await _fixture.ResetDataAsync();
        SeedVehicle(MinimalVehicle("Ferrari 法拉利", "FXX", "S2", "Road", "AWD"));
        SeedVehicle(MinimalVehicle("Porsche", "911 GT3", "S2", "Road", "RWD"));

        using MySQLHelper sql = _fixture.CreateHelper();
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql, _forzaConfig);

        var (success, _, data, totalPages) = svc.GetVehicleInfoRaw("Ferrari911", 1);

        (success == false || data == null || data.Rows.Count == 0 || totalPages == 0)
            .Should().BeTrue("Ferrari 和 911 分别存在，但 Ferrari911 这个组合不存在");
    }

    // =========================================================================
    // B09 — 通配符转义：% 和 _ 被字面匹配而非当作通配符
    // =========================================================================

    [Fact]
    public async Task GetVehicleInfoRaw_百分号被转义为字面量而非通配符()
    {
        await _fixture.ResetDataAsync();
        // 三辆车：Model 分别是 "AAA"（A 字母三连）、"AXA"（中间一个其他字母）、"A%A"（含字面百分号）
        SeedVehicle(MinimalVehicle("Wildcard", "AAA", "A", "Road", "FWD"));
        SeedVehicle(MinimalVehicle("Wildcard", "AXA", "A", "Road", "FWD"));
        SeedVehicle(MinimalVehicle("Wildcard", "A%A", "A", "Road", "FWD"));

        using MySQLHelper sql = _fixture.CreateHelper();
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql, _forzaConfig);

        // 搜索 "A%A"：若 % 未转义则会匹配 "AAA"、"AXA"、"A%A" 三行；
        // 转义正确则只匹配字面量 "A%A" 一行。
        var (success, _, data, _) = svc.GetVehicleInfoRaw("A%A", 1);

        success.Should().BeTrue();
        data.Should().NotBeNull();
        data!.Rows.Count.Should().Be(1, "% 应被转义为字面量，只匹配 \"A%A\"，而非 \"AAA\" 或 \"AXA\"");
        data.Rows[0]["Model"].ToString().Should().Be("A%A");
    }

    [Fact]
    public async Task GetVehicleInfoRaw_下划线被转义为字面量而非通配符()
    {
        await _fixture.ResetDataAsync();
        // 三辆车：Model 分别是 "A_A"（含字面下划线）、"ABA"（任意单字符）、"ACA"
        SeedVehicle(MinimalVehicle("Underscore", "A_A", "B", "Road", "FWD"));
        SeedVehicle(MinimalVehicle("Underscore", "ABA", "B", "Road", "FWD"));
        SeedVehicle(MinimalVehicle("Underscore", "ACA", "B", "Road", "FWD"));

        using MySQLHelper sql = _fixture.CreateHelper();
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql, _forzaConfig);

        // 搜索 "A_A"：若 _ 未转义则会匹配 "ABA"、"ACA"、"A_A" 三行；
        // 转义正确则只匹配字面量 "A_A" 一行。
        var (success, _, data, _) = svc.GetVehicleInfoRaw("A_A", 1);

        success.Should().BeTrue();
        data.Should().NotBeNull();
        data!.Rows.Count.Should().Be(1, "_ 应被转义为字面量，只匹配 \"A_A\"，而非 \"ABA\" 或 \"ACA\"");
        data.Rows[0]["Model"].ToString().Should().Be("A_A");
    }

    // =========================================================================
    // B09 — OR vs AND 类别/比赛类型筛选
    // =========================================================================

    /// <summary>
    /// 种子数据：
    ///   车1 — Class="S2", MatchType="Road"   （同时满足两个条件）
    ///   车2 — Class="S2", MatchType="Rally"  （只满足 Class=S2）
    ///   车3 — Class="A",  MatchType="Road"   （只满足 MatchType=Road）
    ///   车4 — Class="A",  MatchType="Cross"  （两个条件都不满足）
    ///
    /// OR(S2, Road)  → Class=S2 OR MatchType=Road → 车1、车2、车3 共 3 辆
    /// AND(S2, Road) → Class=S2 AND MatchType=Road → 只有车1 共 1 辆
    ///
    /// 注意：GetVehiclesByClassOrMatchType 返回 (bool, string) Markdown 文本；
    ///       GetVehiclesByClassAndMatchTypeRaw 返回 DataTable，两个公开的 Raw 方法做法不对称。
    ///       OR 路径通过 GetVehiclesByClassOrMatchType 的 bool 成功状态 + 直接 SQL 计数来验证；
    ///       AND 路径通过 GetVehiclesByClassAndMatchTypeRaw 返回的 DataTable 行数验证。
    /// </summary>
    [Fact]
    public async Task GetVehiclesByClassOrMatchType_OR逻辑_仅满足单一条件的行也被命中()
    {
        await _fixture.ResetDataAsync();
        SeedVehicle(MinimalVehicle("Maker", "Mv1", "S2", "Road", "AWD"));   // 两条件均满足
        SeedVehicle(MinimalVehicle("Maker", "Mv2", "S2", "Rally", "AWD")); // 只满足 Class=S2
        SeedVehicle(MinimalVehicle("Maker", "Mv3", "A", "Road", "AWD"));   // 只满足 MatchType=Road
        SeedVehicle(MinimalVehicle("Maker", "Mv4", "A", "Cross", "AWD")); // 两者均不满足

        using MySQLHelper sql = _fixture.CreateHelper();
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql, _forzaConfig);

        // OR 路径：搜 Class=S2 OR MatchType=Road，应有 3 辆（Mv1、Mv2、Mv3）
        var (success, _) = svc.GetVehiclesByClassOrMatchType("S2", "Road", 1, out _);
        success.Should().BeTrue("OR 查询至少应命中 Mv1/Mv2/Mv3");

        // 直接用 SQL 计数验证 OR 语义
        using MySQLHelper probe = _fixture.CreateHelper();
        DataRow? row = probe.ExecuteDataRow(
            "SELECT COUNT(*) FROM Vehicles WHERE Class='S2' OR MatchType='Road'");
        int orCount = Convert.ToInt32(row![0]);
        orCount.Should().Be(3, "OR 应返回满足任一条件的行（Mv1、Mv2、Mv3）");
    }

    [Fact]
    public async Task GetVehiclesByClassAndMatchTypeRaw_AND逻辑_只返回同时满足两个条件的行()
    {
        await _fixture.ResetDataAsync();
        SeedVehicle(MinimalVehicle("Maker", "Mv1", "S2", "Road", "AWD"));   // 两条件均满足
        SeedVehicle(MinimalVehicle("Maker", "Mv2", "S2", "Rally", "AWD")); // 只满足 Class=S2
        SeedVehicle(MinimalVehicle("Maker", "Mv3", "A", "Road", "AWD"));   // 只满足 MatchType=Road
        SeedVehicle(MinimalVehicle("Maker", "Mv4", "A", "Cross", "AWD")); // 两者均不满足

        using MySQLHelper sql = _fixture.CreateHelper();
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql, _forzaConfig);

        var (success, _, data, _) = svc.GetVehiclesByClassAndMatchTypeRaw("S2", "Road", 1);

        success.Should().BeTrue();
        data.Should().NotBeNull();
        // AND：Class=S2 AND MatchType=Road → 只有 Mv1 1 辆
        data!.Rows.Count.Should().Be(1, "AND 应只返回同时满足两个条件的行");
        data.Rows[0]["Model"].ToString().Should().Be("Mv1");
    }

    [Fact]
    public async Task OR和AND在同一种子数据上结果集大小不同()
    {
        // 该测试是 B09 核心：证明 OR 与 AND 在真实数据库上行为不同
        await _fixture.ResetDataAsync();
        SeedVehicle(MinimalVehicle("Maker", "Mv1", "S2", "Road", "AWD"));  // 满足两个条件
        SeedVehicle(MinimalVehicle("Maker", "Mv2", "S2", "Rally", "AWD")); // 只满足 Class
        SeedVehicle(MinimalVehicle("Maker", "Mv3", "A", "Road", "AWD"));   // 只满足 MatchType

        // OR 计数（直接 SQL）
        using MySQLHelper sqlOr = _fixture.CreateHelper();
        DataRow? orRow = sqlOr.ExecuteDataRow(
            "SELECT COUNT(*) FROM Vehicles WHERE Class='S2' OR MatchType='Road'");
        int orCount = Convert.ToInt32(orRow![0]);

        // AND 计数（通过 service Raw 方法）
        using MySQLHelper sqlAnd = _fixture.CreateHelper();
        ForzaService svcAnd = new(NullLogger<ForzaService>.Instance, sqlAnd, _forzaConfig);
        var (_, _, andData, _) = svcAnd.GetVehiclesByClassAndMatchTypeRaw("S2", "Road", 1);

        andData.Should().NotBeNull();
        orCount.Should().BeGreaterThan(andData!.Rows.Count,
            "OR 的结果集（3）应大于 AND 的结果集（1）：OR 包括只满足单一条件的行");
    }

    // =========================================================================
    // B09 — GetVehicleById (@id fix 的负荷证明)
    // =========================================================================

    [Fact]
    public async Task GetVehicleById_返回指定车辆而非全部行()
    {
        await _fixture.ResetDataAsync();
        // 种两辆车，确保表中有 ≥2 行，证明 @id 绑定是有效区分条件
        int id1 = SeedVehicle(MinimalVehicle("Alpha", "X1", "S1", "Road", "AWD"));
        int id2 = SeedVehicle(MinimalVehicle("Beta", "Y2", "A", "Rally", "FWD"));

        using MySQLHelper sql = _fixture.CreateHelper();
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql, _forzaConfig);

        var (success, _, data, _) = svc.GetVehicleById(id1);

        success.Should().BeTrue();
        data.Should().NotBeNull();
        // 若 @id 未绑定，WHERE Id=@id 会失败或返回全部行；正确绑定只返回 1 行
        data!.Rows.Count.Should().Be(1, "只应返回 Id 匹配的那一行，而不是全部行");
        data.Rows[0]["Manufacturer"].ToString().Should().Be("Alpha");
        data.Rows[0]["Model"].ToString().Should().Be("X1");
        // 确保没有把 id2 的行包含进来
        data.AsEnumerable().Should().NotContain(r => r["Manufacturer"].ToString() == "Beta");
    }

    [Fact]
    public async Task GetVehicleById_不存在的ID返回空结果集()
    {
        await _fixture.ResetDataAsync();
        SeedVehicle(MinimalVehicle("OnlyOne", "Z9", "S2", "Road", "RWD"));

        using MySQLHelper sql = _fixture.CreateHelper();
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql, _forzaConfig);

        var (success, _, data, totalPages) = svc.GetVehicleById(999999);

        // 查询本身执行成功，但无匹配行（success=true, Rows.Count=0, totalPages=0）
        (success == false || data == null || data.Rows.Count == 0).Should().BeTrue(
            "不存在的 ID 应返回空结果或失败，而非随机返回行");
    }

    [Fact]
    public async Task GetVehicleById_通过控制器调用_存在时返回OkResult()
    {
        await _fixture.ResetDataAsync();
        int id = SeedVehicle(MinimalVehicle("Honda", "NSX", "S2", "Road", "AWD"));

        using MySQLHelper sql = _fixture.CreateHelper();
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql, _forzaConfig);
        ForzaController controller = new(svc);

        IActionResult result = controller.GetVehicleById(id);

        result.Should().BeOfType<OkObjectResult>("存在的车辆应返回 200 OK");
    }

    [Fact]
    public async Task GetVehicleById_通过控制器调用_不存在时返回NotFoundResult()
    {
        await _fixture.ResetDataAsync();

        using MySQLHelper sql = _fixture.CreateHelper();
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql, _forzaConfig);
        ForzaController controller = new(svc);

        IActionResult result = controller.GetVehicleById(999999);

        result.Should().BeOfType<NotFoundObjectResult>("不存在的车辆应返回 404 Not Found");
    }

    // =========================================================================
    // Task 7 — EnsureVehicleModel / GetVehicleModelById / UpdateVehicleModel
    // =========================================================================

    /// <summary>创建最小合法 VehicleModelRequest，可按需覆写字段。</summary>
    private static VehicleModelRequest MinimalVehicleModel(
        string manufacturer = "TestMaker",
        string model = "TestModel",
        string? year = null) => new()
        {
            Manufacturer = manufacturer,
            Model = model,
            Year = year,
        };

    [Fact]
    public async Task EnsureVehicleModel_同ModelKey调用两次_只建一行且返回同Id()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql1 = _fixture.CreateHelper();
        ForzaService svc1 = new(NullLogger<ForzaService>.Instance, sql1, _forzaConfig);

        VehicleModelRequest req = MinimalVehicleModel("Ferrari 法拉利", "FXX", "2022");
        int id1 = svc1.EnsureVehicleModel(req, 0, out string err1);

        err1.Should().BeEmpty("第一次 upsert 应成功");
        id1.Should().BeGreaterThan(0);

        // 第二次用不同的 helper 模拟不同调用方
        using MySQLHelper sql2 = _fixture.CreateHelper();
        ForzaService svc2 = new(NullLogger<ForzaService>.Instance, sql2, _forzaConfig);
        int id2 = svc2.EnsureVehicleModel(req, 0, out string err2);

        err2.Should().BeEmpty("第二次 upsert 应成功");
        id2.Should().Be(id1, "同 ModelKey 第二次调用应返回同一 VehicleId");

        // 直接验证 Vehicles 表中只有一行该 ModelKey
        using MySQLHelper probe = _fixture.CreateHelper();
        DataRow? row = probe.ExecuteDataRow(
            "SELECT COUNT(*) FROM Vehicles WHERE Manufacturer='Ferrari 法拉利' AND Model='FXX'");
        Convert.ToInt32(row![0]).Should().Be(1, "同 ModelKey 两次 upsert 应只产生一行");
    }

    [Fact]
    public async Task EnsureVehicleModel_Year为null与空串_视为同一车型()
    {
        await _fixture.ResetDataAsync();

        // 第一次：Year = null
        using MySQLHelper sql1 = _fixture.CreateHelper();
        ForzaService svc1 = new(NullLogger<ForzaService>.Instance, sql1, _forzaConfig);
        int idNull = svc1.EnsureVehicleModel(MinimalVehicleModel("Honda", "Civic", null), 0, out string e1);
        e1.Should().BeEmpty();
        idNull.Should().BeGreaterThan(0);

        // 第二次：Year = ""（空串）
        using MySQLHelper sql2 = _fixture.CreateHelper();
        ForzaService svc2 = new(NullLogger<ForzaService>.Instance, sql2, _forzaConfig);
        int idEmpty = svc2.EnsureVehicleModel(MinimalVehicleModel("Honda", "Civic", ""), 0, out string e2);
        e2.Should().BeEmpty();

        idEmpty.Should().Be(idNull, "Year=null 与 Year=\"\" 归一化后相同，应命中同一行");

        // 仅一行
        using MySQLHelper probe = _fixture.CreateHelper();
        DataRow? row = probe.ExecuteDataRow(
            "SELECT COUNT(*) FROM Vehicles WHERE Manufacturer='Honda' AND Model='Civic'");
        Convert.ToInt32(row![0]).Should().Be(1);
    }

    [Fact]
    public async Task EnsureVehicleModel_返回的Id经GetVehicleModelById命中正确制造商()
    {
        await _fixture.ResetDataAsync();

        using MySQLHelper sql = _fixture.CreateHelper();
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql, _forzaConfig);
        int id = svc.EnsureVehicleModel(MinimalVehicleModel("Lamborghini", "Huracán"), 42, out string err);

        err.Should().BeEmpty();
        id.Should().BeGreaterThan(0);

        // 用独立 probe 验证 GetVehicleModelById 返回正确行
        using MySQLHelper probe = _fixture.CreateHelper();
        ForzaService probeSvc = new(NullLogger<ForzaService>.Instance, probe, _forzaConfig);
        var (success, message, data, _) = probeSvc.GetVehicleModelById(id);

        success.Should().BeTrue(message);
        data.Should().NotBeNull();
        data!.Rows.Count.Should().Be(1);
        data.Rows[0]["Manufacturer"].ToString().Should().Be("Lamborghini");
        data.Rows[0]["Model"].ToString().Should().Be("Huracán");
    }
    // =========================================================================
    // Task 8 — InsertTune / UpdateTune / DeleteTunesByIds
    // =========================================================================

    /// <summary>
    /// 构建最小合法 VehicleTuneRequest（含必填字段 Class 和 MatchType）。
    /// 调用方需在外部先通过 EnsureVehicleModel 获得 VehicleId 并赋值。
    /// </summary>
    private static VehicleTuneRequest MinimalTuneRequest(
        int vehicleId,
        string cls = "S1",
        string matchType = "Road",
        string drivetrain = "AWD",
        string tuneRaw = "TestTuner",
        string? sourceFileId = null) => new()
        {
            VehicleId = vehicleId,
            Class = cls,
            MatchType = matchType,
            Drivetrain = drivetrain,
            TuneRaw = tuneRaw,
            SourceFileId = sourceFileId,
        };

    /// <summary>
    /// 在测试内插入一条调校，用独立 helper 以避免事务/参数状态纠缠。
    /// 外层需要先 EnsureVehicleModel 获得 vehicleId。
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

    [Fact]
    public async Task UpdateTune_改驱动显示值_SourceIdentity与指纹均不变_状态回正常_清缓存行()
    {
        await _fixture.ResetDataAsync();

        // 先建车型
        using MySQLHelper sqlModel = _fixture.CreateHelper();
        ForzaService svcModel = new(NullLogger<ForzaService>.Instance, sqlModel, _forzaConfig);
        int vehicleId = svcModel.EnsureVehicleModel(MinimalVehicleModel("TuneTestMaker", "TuneTestModel"), 0, out string modelErr);
        modelErr.Should().BeEmpty();
        vehicleId.Should().BeGreaterThan(0);

        // 插入带来源信息的调校（有 SourceFileId → identity/fingerprint 非空）
        VehicleTuneRequest insertReq = MinimalTuneRequest(vehicleId, drivetrain: "RWD", sourceFileId: "file-abc");
        int tuneId = SeedTune(insertReq, seq: 0);

        // 读取插入后的 SourceIdentity 和 SourceFingerprint（用于断言不变）
        using MySQLHelper probe0 = _fixture.CreateHelper();
        DataRow? row0 = probe0.ExecuteDataRow(
            $"SELECT SourceIdentity, SourceFingerprint, TuneStatus FROM VehicleTunes WHERE Id = {tuneId}");
        row0.Should().NotBeNull("调校应存在");
        string originalIdentity = row0!["SourceIdentity"].ToString()!;
        string originalFingerprint = row0["SourceFingerprint"].ToString()!;
        originalIdentity.Should().NotBeEmpty("有 SourceFileId 时 SourceIdentity 应非空");
        originalFingerprint.Should().NotBeEmpty("有 SourceFileId 时 SourceFingerprint 应非空");

        // 在 VehicleTuneCache 插入一行（模拟已有待更新缓存），用原生 SQL
        using MySQLHelper sqlCache = _fixture.CreateHelper();
        sqlCache.Execute($@"
            INSERT INTO VehicleTuneCache
                (TuneId, VehicleId, Class, MatchType, SourceFileId, SourceIdentity)
            VALUES
                ({tuneId}, {vehicleId}, 'S1', 'Road', 'file-abc', 'dummy-identity')");
        sqlCache.Success.Should().BeTrue("插入缓存行应成功");

        // 调用 UpdateTune 改 Drivetrain（不传 TuneStatus，预期回写为"正常"）
        using MySQLHelper sqlUpdate = _fixture.CreateHelper();
        ForzaService svcUpdate = new(NullLogger<ForzaService>.Instance, sqlUpdate, _forzaConfig);
        VehicleTuneRequest updateReq = new() { Id = tuneId, Drivetrain = "四驱" };
        bool ok = svcUpdate.UpdateTune(updateReq, actorUserId: 42, out string updateErr);
        ok.Should().BeTrue(updateErr);

        // 探针：验证所有后置条件
        using MySQLHelper probe = _fixture.CreateHelper();

        // 1. SourceIdentity 未变
        DataRow? afterRow = probe.ExecuteDataRow(
            $"SELECT SourceIdentity, SourceFingerprint, TuneStatus, Drivetrain FROM VehicleTunes WHERE Id = {tuneId}");
        afterRow.Should().NotBeNull();
        afterRow!["SourceIdentity"].ToString().Should().Be(originalIdentity, "SourceIdentity 冻结，UpdateTune 不得修改");
        afterRow["SourceFingerprint"].ToString().Should().Be(originalFingerprint, "SourceFingerprint 冻结，UpdateTune 不得修改");

        // 2. Drivetrain 已更新
        afterRow["Drivetrain"].ToString().Should().Be("四驱");

        // 3. TuneStatus 回写为"正常"（未显式给状态）
        afterRow["TuneStatus"].ToString().Should().Be("正常");

        // 4. VehicleTuneCache 对应行已清除
        DataRow? cacheRow = probe.ExecuteDataRow(
            $"SELECT COUNT(*) FROM VehicleTuneCache WHERE TuneId = {tuneId}");
        Convert.ToInt32(cacheRow![0]).Should().Be(0, "UpdateTune 后对应调校的缓存行应被清除");
    }

    [Fact]
    public async Task DeleteTunesByIds_删除后查不到该调校()
    {
        await _fixture.ResetDataAsync();

        // 建车型
        using MySQLHelper sqlModel = _fixture.CreateHelper();
        ForzaService svcModel = new(NullLogger<ForzaService>.Instance, sqlModel, _forzaConfig);
        int vehicleId = svcModel.EnsureVehicleModel(MinimalVehicleModel("DelTuneMaker", "DelTuneModel"), 0, out string modelErr);
        modelErr.Should().BeEmpty();

        // 插入调校
        int tuneId = SeedTune(MinimalTuneRequest(vehicleId));

        // 删除
        using MySQLHelper sqlDel = _fixture.CreateHelper();
        ForzaService svcDel = new(NullLogger<ForzaService>.Instance, sqlDel, _forzaConfig);
        var (success, _, deletedCount) = svcDel.DeleteTunesByIds([tuneId]);
        success.Should().BeTrue();
        deletedCount.Should().Be(1);

        // 验证已不存在
        using MySQLHelper probe = _fixture.CreateHelper();
        DataRow? row = probe.ExecuteDataRow($"SELECT COUNT(*) FROM VehicleTunes WHERE Id = {tuneId}");
        Convert.ToInt32(row![0]).Should().Be(0, "删除后 VehicleTunes 中不应存在该行");
    }

    [Fact]
    public async Task InsertTune_手动创建无来源_SourceIdentity为空()
    {
        await _fixture.ResetDataAsync();

        // 建车型
        using MySQLHelper sqlModel = _fixture.CreateHelper();
        ForzaService svcModel = new(NullLogger<ForzaService>.Instance, sqlModel, _forzaConfig);
        int vehicleId = svcModel.EnsureVehicleModel(MinimalVehicleModel("ManualMaker", "ManualModel"), 42, out string modelErr);
        modelErr.Should().BeEmpty();

        // InsertTune 不传 SourceFileId（手动路径）
        VehicleTuneRequest req = MinimalTuneRequest(vehicleId, sourceFileId: null);
        req.IsManual = true;

        using MySQLHelper sql = _fixture.CreateHelper();
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql, _forzaConfig);
        sql.NewTransaction();
        int tuneId = svc.InsertTune(req, seq: 0, out string err);
        sql.Commit();
        tuneId.Should().BeGreaterThan(0, err);

        // 验证 SourceIdentity 为空串
        using MySQLHelper probe = _fixture.CreateHelper();
        DataRow? row = probe.ExecuteDataRow($"SELECT SourceIdentity, SourceFingerprint FROM VehicleTunes WHERE Id = {tuneId}");
        row.Should().NotBeNull();
        row!["SourceIdentity"].ToString().Should().BeEmpty("无来源时 SourceIdentity 应存为空串");
        (row["SourceFingerprint"] == DBNull.Value || row["SourceFingerprint"].ToString() == "").Should().BeTrue("无来源时 SourceFingerprint 应为 NULL");
    }

}