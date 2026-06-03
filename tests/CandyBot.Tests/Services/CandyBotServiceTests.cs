using CandyBot.Tests.Fakes;
using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Services;
using CandyCottage.CandyBot.Services.BotServices;
using CandyCottage.CandyBot.Utilities;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Milimoe.FunGame.Core.Api.Transmittal;
using NSubstitute;
using Xunit;

namespace CandyBot.Tests.Services;

/// <summary>
/// B01–B05：CandyBotService 的权限闸口（CheckUser）、功能开关（WillGoOn）、
/// 处理器链路由顺序、未知指令短路与维护提示。
/// </summary>
public class CandyBotServiceTests
{
    /// <summary>
    /// 构造一个被测 CandyBotService。除显式传入者外，其余依赖默认 null!，
    /// 仅在被测代码路径触达时才会被解引用——多数命令路径不会触达数据库与下游服务。
    /// </summary>
    private static CandyBotService CreateService(
        IBotReplySender botService,
        CandyBotFunctionConfig? functionConfig = null,
        ForzaConfig? forzaConfig = null,
        IMemoryCache? memoryCache = null,
        SQLHelper sql = null!,
        BettingService bettingService = null!,
        IForzaSheetImporter forzaSheetImporter = null!) =>
        new(
            NullLogger<CandyBotService>.Instance,
            memoryCache!,
            sql,
            functionConfig!,
            forzaConfig!,
            candyBotConfig: null!,
            taskQueue: null!,
            taskManager: null!,
            botService,
            bettingService,
            sqlInitService: null!,
            userService: null!,
            forzaService: null!,
            forzaSheetImporter,
            esportsSyncService: null!);

    private static MemoryCache NewCache() => new(new MemoryCacheOptions());

    private static CandyBotFunctionConfig AllOn() => new()
    {
        System = true,
        AllowAdminsDebug = true,
        CandyCottage = true,
        UserSaved = true,
        UserRename = true,
        ForzaHorizon = true,
        Betting = true,
        Betting_ReadOnly = false,
    };

    private static ForzaConfig ForzaSheets() => new()
    {
        DocsID =
        {
            { "ZGC", DefaultVehicleSheetFileId }
        }
    };

    public const string DefaultVehicleSheetFileId = "DSXRieHNsZXpFZmZ4";

    // ---- WillGoOn (plan step 1) ----------------------------------------

    [Fact]
    public void WillGoOn_功能开启时任何用户都放行()
    {
        RecordingBotReplySender sender = new();
        CandyBotService svc = CreateService(sender, AllOn());

        svc.WillGoOn(isFunctionOpen: true, user: null).Should().BeTrue();
        svc.WillGoOn(isFunctionOpen: true, user: TestData.CreateUser()).Should().BeTrue();
    }

    [Fact]
    public void WillGoOn_功能关闭且管理员调试开启_管理员放行其他人拒绝()
    {
        RecordingBotReplySender sender = new();
        CandyBotFunctionConfig cfg = AllOn();
        cfg.AllowAdminsDebug = true;
        CandyBotService svc = CreateService(sender, cfg);

        svc.WillGoOn(isFunctionOpen: false, user: TestData.CreateUser(isAdmin: true)).Should().BeTrue();
        svc.WillGoOn(isFunctionOpen: false, user: TestData.CreateUser(isAdmin: false)).Should().BeFalse();
        svc.WillGoOn(isFunctionOpen: false, user: null).Should().BeFalse();
    }

    [Fact]
    public void WillGoOn_功能关闭且管理员调试关闭_连管理员也拒绝()
    {
        RecordingBotReplySender sender = new();
        CandyBotFunctionConfig cfg = AllOn();
        cfg.AllowAdminsDebug = false;
        CandyBotService svc = CreateService(sender, cfg);

        svc.WillGoOn(isFunctionOpen: false, user: TestData.CreateUser(isAdmin: true)).Should().BeFalse();
    }

    // ---- CheckUser (plan step 2 / B03 / B04) ---------------------------

    [Fact]
    public async Task CheckUser_用户为空_回复创建存档提示并带快捷按钮_B03()
    {
        RecordingBotReplySender sender = new();
        CandyBotService svc = CreateService(sender);
        C2CMessage e = TestData.CreateMessage("糖果屋");

        bool ok = await svc.CheckUser(e, user: null);

        ok.Should().BeFalse();
        sender.Sent.Should().HaveCount(1);
        BotReply reply = sender.Sent[0].Reply;
        reply.Markdown!.Content.Should().Be("创建存档后可使用相关功能。");
        // 键盘中应包含「创建存档」指令按钮
        List<Button> buttons = reply.Keyboard!.Content!.Rows.SelectMany(r => r.Buttons).ToList();
        buttons.Should().Contain(b => b.Action.Data == "创建存档");
    }

    [Fact]
    public async Task CheckUser_非管理员在仅管理员校验下被拒绝_B04()
    {
        RecordingBotReplySender sender = new();
        CandyBotService svc = CreateService(sender);
        C2CMessage e = TestData.CreateMessage("某指令");

        bool ok = await svc.CheckUser(e, TestData.CreateUser(isAdmin: false), checkAdmin: true);

        ok.Should().BeFalse();
        sender.TextContents.Should().ContainSingle().Which.Should().Be("你没有权限执行此操作。");
    }

    [Fact]
    public async Task CheckUser_运营在仅管理员校验下被拒绝()
    {
        RecordingBotReplySender sender = new();
        CandyBotService svc = CreateService(sender);
        C2CMessage e = TestData.CreateMessage("某指令");

        bool ok = await svc.CheckUser(e, TestData.CreateUser(isOperator: true), checkAdmin: true);

        ok.Should().BeFalse();
        sender.TextContents.Should().ContainSingle().Which.Should().Be("你没有权限执行此操作。");
    }

    [Fact]
    public async Task CheckUser_运营在管理或运营校验下被放行()
    {
        RecordingBotReplySender sender = new();
        CandyBotService svc = CreateService(sender);
        C2CMessage e = TestData.CreateMessage("某指令");

        bool ok = await svc.CheckUser(e, TestData.CreateUser(isOperator: true), checkAdmin: false);

        ok.Should().BeTrue();
        sender.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckUser_管理员在任何校验下都被放行()
    {
        RecordingBotReplySender sender = new();
        CandyBotService svc = CreateService(sender);
        C2CMessage e = TestData.CreateMessage("某指令");

        bool ok = await svc.CheckUser(e, TestData.CreateUser(isAdmin: true), checkAdmin: true);

        ok.Should().BeTrue();
        sender.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckUser_普通注册用户在仅校验注册下被放行()
    {
        RecordingBotReplySender sender = new();
        CandyBotService svc = CreateService(sender);
        C2CMessage e = TestData.CreateMessage("某指令");

        bool ok = await svc.CheckUser(e, TestData.CreateUser(), checkAdmin: false, checkOperator: false);

        ok.Should().BeTrue();
        sender.Sent.Should().BeEmpty();
    }

    // ---- Handler: 未知指令短路 (B01) -----------------------------------

    [Fact]
    public async Task Handler_管理员发送未知指令_全部开关开启_不产生任何回复_B01()
    {
        RecordingBotReplySender sender = new();
        MemoryCache cache = NewCache();
        const string openid = "admin-openid";
        cache.Set($"bot::{openid}", TestData.CreateUser(isAdmin: true, openid: openid), TimeSpan.FromMinutes(10));

        CandyBotService svc = CreateService(sender, AllOn(), ForzaSheets(), cache);
        C2CMessage e = TestData.CreateMessage("这是一条没有任何处理器匹配的未知指令", openid);

        await svc.Handler(e);

        sender.Sent.Should().BeEmpty("管理员的未知指令穿过整条链后既不匹配任何前缀也不触发权限拒绝");
    }

    // ---- Handler: 维护提示 / 管理员调试绕过 (B05) -----------------------

    [Fact]
    public async Task Handler_系统关闭且调试关闭_管理员发非开关指令_收到维护提示_B05a()
    {
        RecordingBotReplySender sender = new();
        MemoryCache cache = NewCache();
        const string openid = "admin-openid";
        cache.Set($"bot::{openid}", TestData.CreateUser(isAdmin: true, openid: openid), TimeSpan.FromMinutes(10));

        CandyBotFunctionConfig cfg = AllOn();
        cfg.System = false;
        cfg.AllowAdminsDebug = false;

        CandyBotService svc = CreateService(sender, cfg, ForzaSheets(), cache);
        C2CMessage e = TestData.CreateMessage("随便一条非开关指令", openid);

        await svc.Handler(e);

        // CheckUser 因管理员而通过 → HandleToggleCommand 返回 false → 落到维护提示分支
        sender.TextContents.Should().ContainSingle().Which.Should().Be("糖果屋正在维护，请稍后再试。");
    }

    [Fact]
    public async Task Handler_系统关闭但调试开启_管理员发帮助_绕过维护并得到帮助_B05b()
    {
        RecordingBotReplySender sender = new();
        MemoryCache cache = NewCache();
        const string openid = "admin-openid";
        cache.Set($"bot::{openid}", TestData.CreateUser(isAdmin: true, openid: openid), TimeSpan.FromMinutes(10));

        CandyBotFunctionConfig cfg = AllOn();
        cfg.System = false;
        cfg.AllowAdminsDebug = true;

        CandyBotService svc = CreateService(sender, cfg, ForzaSheets(), cache);
        C2CMessage e = TestData.CreateMessage("帮助", openid);

        await svc.Handler(e);

        sender.Sent.Should().HaveCount(1);
        string? content = sender.TextContents.Single();
        content.Should().NotBe("糖果屋正在维护，请稍后再试。");
        content.Should().Contain("欢迎来到糖果屋");
    }

    // ---- Handler: 路由顺序 赛事详情 优先于 赛事 (B02) -------------------

    [Fact]
    public async Task Handler_赛事详情应路由到详情分支而非赛事列表分支_B02()
    {
        RecordingBotReplySender sender = new();
        MemoryCache cache = NewCache();
        const string openid = "admin-openid";
        cache.Set($"bot::{openid}", TestData.CreateUser(isAdmin: true, openid: openid), TimeSpan.FromMinutes(10));

        // 构造一个最小可用的 SQLHelper 替身，驱动 BettingService。
        // 注意：Success 是由 Result 计算得来的非虚属性，需配置 Result（NotFound）。
        SQLHelper sqlSub = Substitute.For<SQLHelper>();
        sqlSub.Parameters.Returns(new Dictionary<string, object>());
        sqlSub.Execute(Arg.Any<string>()).Returns(0);
        // Result = NotFound → Success 为 false → GetEventDetail 在「赛事信息」查询后返回「赛事不存在。」
        sqlSub.Result.Returns(Milimoe.FunGame.Core.Library.Constant.SQLResult.NotFound);

        CandyBotFunctionConfig cfg = AllOn();
        BettingService betting = new(sqlSub, cfg);
        CandyBotService svc = CreateService(sender, cfg, ForzaSheets(), cache, bettingService: betting);

        C2CMessage e = TestData.CreateMessage("赛事详情 1", openid);
        await svc.Handler(e);

        sender.Sent.Should().HaveCount(1);
        string? content = sender.TextContents.Single();
        // 赛事详情分支（GetEventDetail）的输出，绝不会出现在「赛事列表」分支（GetEventsOverview）中。
        content.Should().Be("赛事不存在。");
        content.Should().NotContain("赛事列表");
    }

    // ---- Handler: 同步车辆表 (Forza sheet sync) -------------------------

    [Fact]
    public async Task HandleForzaHorizonInput_同步车辆表_管理员触发导入并收到摘要()
    {
        RecordingBotReplySender sender = new();
        IForzaSheetImporter importer = Substitute.For<IForzaSheetImporter>();
        importer.SyncAsync(DefaultVehicleSheetFileId, Arg.Any<CancellationToken>())
            .Returns(new ForzaSheetSyncReport(
                Success: true,
                PreviousVehicleCount: 128,
                ParsedVehicleCount: 157,
                InsertedVehicleCount: 157,
                ParsedSheetNames: ["B-600公路", "C-500公路"],
                SkippedSheetNames: ["季节赛推荐调校（每周更新）"],
                SkippedRows: [],
                Diagnostics: [],
                ErrorMessage: null));

        SQLHelper sqlSub = Substitute.For<SQLHelper>();
        sqlSub.Parameters.Returns(new Dictionary<string, object>());
        sqlSub.Execute(Arg.Any<string>()).Returns(0);
        sqlSub.Result.Returns(Milimoe.FunGame.Core.Library.Constant.SQLResult.NotFound);
        CandyBotService svc = CreateService(sender, AllOn(), ForzaSheets(), sql: sqlSub, forzaSheetImporter: importer);
        C2CMessage e = TestData.CreateMessage("同步车辆表");

        bool handled = await svc.HandleForzaHorizonInput(e, TestData.CreateUser(isAdmin: true));

        handled.Should().BeTrue();
        await importer.Received(1).SyncAsync(DefaultVehicleSheetFileId, Arg.Any<CancellationToken>());
        sender.TextContents.Single().Should().Contain("已同步 157 辆");
    }
}
