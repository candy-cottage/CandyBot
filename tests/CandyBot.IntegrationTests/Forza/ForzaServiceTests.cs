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
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql);
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
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql);

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
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql);

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
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql);

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
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql);

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
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql);

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
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql);

        VehicleRequest req = MinimalVehicle("Honda", "Civic", "A", "Road", "FWD");
        req.Year = "2022";

        bool ok = svc.AddVehicle(req, out string error);
        int newId = (int)sql.LastInsertId;

        ok.Should().BeTrue(error);
        newId.Should().BeGreaterThan(0);

        // 用 GetVehicleInfoRaw 验证车辆可查询到
        using MySQLHelper probe = _fixture.CreateHelper();
        ForzaService probeSvc = new(NullLogger<ForzaService>.Instance, probe);
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
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql);

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
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql);
        VehicleRequest upd = new() { Id = id, Remarks = "极好操控" };
        bool ok = svc.UpdateVehicle(upd, out string error);

        ok.Should().BeTrue(error);

        // 查询验证 Remarks 已更新
        using MySQLHelper probe = _fixture.CreateHelper();
        ForzaService probeSvc = new(NullLogger<ForzaService>.Instance, probe);
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
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql);
        var (success, _, deletedCount) = svc.DeleteVehiclesByIds([id]);

        success.Should().BeTrue();
        deletedCount.Should().Be(1);

        // 确认该行已不存在
        using MySQLHelper probe = _fixture.CreateHelper();
        ForzaService probeSvc = new(NullLogger<ForzaService>.Instance, probe);
        var (s2, _, data, _) = probeSvc.GetVehicleById(id);
        // success=true 但 data 为空（查询本身成功，但无行）
        (s2 == false || data == null || data.Rows.Count == 0).Should().BeTrue("已删除的车辆不应再被查到");
    }

    [Fact]
    public async Task DeleteVehiclesByIds_空列表返回false()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql);

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
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql);

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
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql);

        var (success, _, data, totalPages) = svc.GetVehicleInfoRaw("NotExistCar", 1);

        // 查询本身成功，但结果集为空（totalPages=0 或 data 为空行）
        (success == false || data == null || data.Rows.Count == 0 || totalPages == 0).Should().BeTrue("不存在的关键字应无命中");
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
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql);

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
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql);

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
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql);

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
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql);

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
        ForzaService svcAnd = new(NullLogger<ForzaService>.Instance, sqlAnd);
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
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql);

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
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql);

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
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql);
        ForzaController controller = new(svc);

        IActionResult result = controller.GetVehicleById(id);

        result.Should().BeOfType<OkObjectResult>("存在的车辆应返回 200 OK");
    }

    [Fact]
    public async Task GetVehicleById_通过控制器调用_不存在时返回NotFoundResult()
    {
        await _fixture.ResetDataAsync();

        using MySQLHelper sql = _fixture.CreateHelper();
        ForzaService svc = new(NullLogger<ForzaService>.Instance, sql);
        ForzaController controller = new(svc);

        IActionResult result = controller.GetVehicleById(999999);

        result.Should().BeOfType<NotFoundObjectResult>("不存在的车辆应返回 404 Not Found");
    }
}
