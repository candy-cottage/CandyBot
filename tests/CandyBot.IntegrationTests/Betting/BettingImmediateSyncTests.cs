using System.Data;
using CandyBot.IntegrationTests.Infrastructure;
using CandyCottage.CandyBot.Interfaces;
using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Services;
using CandyCottage.CandyBot.Services.Esports;
using CandyCottage.CandyBot.Utilities;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Milimoe.FunGame.Core.Api.Utility;
using Milimoe.FunGame.Core.Entity;
using Xunit;

namespace CandyBot.IntegrationTests.Betting;

[Collection(MySqlCollection.Name)]
[Trait("Category", "MySql")]
public class BettingImmediateSyncTests(MySqlFixture fixture)
{
    private readonly MySqlFixture _fixture = fixture;

    private sealed class RecordingBotReplySender : IBotReplySender
    {
        public List<BotReply> Sent { get; } = [];
        public IEnumerable<string?> TextContents => Sent.Select(reply => reply.Text ?? reply.Markdown?.Content);

        public Task<SendMessageResult?> SendAsync(IBotMessage msg, BotReply reply, int? msgSeq = null)
        {
            Sent.Add(reply);
            return Task.FromResult((SendMessageResult?)null);
        }

        public Task<bool> RecallMessageAsync(IBotMessage msg, bool hideTip = false)
        {
            return Task.FromResult(false);
        }
    }

    // 可控数据源：上行比赛取自构造入参，结果固定为空，且记录是否被调用以验证短路路径。
    private sealed class FakeProvider(IReadOnlyList<ExternalEsportsMatch> matches) : IEsportsDataProvider
    {
        public bool ScheduleCalled { get; private set; }

        public Task<IReadOnlyList<ExternalEsportsMatch>> GetUpcomingMatchesAsync(string gameSlug, DateTime since, CancellationToken cancellationToken = default)
        {
            ScheduleCalled = true;
            return Task.FromResult(matches);
        }

        public Task<IReadOnlyList<ExternalEsportsResult>> GetFinishedResultsAsync(IReadOnlyCollection<string> externalIds, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ExternalEsportsResult>>([]);
    }

    private static ExternalEsportsMatch Match(string id, DateTime start, string t1 = "NAVI", string t2 = "FaZe") =>
        new(
            ExternalId: id,
            EventExternalId: "league:season:tournament",
            EventName: "ESL Pro League Season 23",
            EventStartTime: start.Date,
            EventEndTime: start.Date.AddDays(7),
            Team1Name: t1,
            Team2Name: t2,
            StartTime: start,
            Stage: "小组赛");

    private static User AdminUser()
    {
        User user = Factory.GetUser();
        user.Id = 1;
        user.Username = "admin";
        user.IsAdmin = true;
        return user;
    }

    private static CandyBotService BuildService(MySQLHelper sql, CandyBotFunctionConfig cfg, RecordingBotReplySender sender, IEsportsDataProvider provider) =>
        new(
            NullLogger<CandyBotService>.Instance,
            new MemoryCache(new MemoryCacheOptions()),
            sql,
            cfg,
            forzaConfig: null!,
            taskQueue: null!,
            taskManager: null!,
            taskSchedulerExecutor: null!,
            sender,
            new BettingService(sql, cfg),
            sqlInitService: null!,
            userService: null!,
            forzaService: null!,
            esportsSyncService: new EsportsSyncService(
                NullLogger<EsportsSyncService>.Instance,
                sql,
                new BettingService(sql, cfg),
                provider,
                new PandaScoreConfig { GameSlug = "csgo" }),
            userAPITokenService: null!,
            forzaSheetImporter: null!);

    [Fact]
    public async Task HandleBettingInput_立即同步赛事_自动化维护中_拒绝且不触发同步()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();

        RecordingBotReplySender sender = new();
        FakeProvider provider = new([]);
        // Betting 开启但 BettingAutomation 关闭，且关闭管理员调试绕过：应在触达同步前短路。
        CandyBotFunctionConfig cfg = new() { System = true, Betting = true, BettingAutomation = false, AllowAdminsDebug = false };
        CandyBotService service = BuildService(sql, cfg, sender, provider);

        bool handled = await service.HandleBettingInput(new C2CMessage { Detail = "立即同步赛事", Author = new() { Id = "admin" } }, AdminUser());

        handled.Should().BeTrue();
        sender.TextContents.Single().Should().Be("赛事自动化正在维护，请稍后再试。");
        provider.ScheduleCalled.Should().BeFalse();
        Convert.ToInt32(sql.ExecuteDataRow("SELECT COUNT(*) AS c FROM betting_matches")!["c"]).Should().Be(0);
    }

    [Fact]
    public async Task HandleBettingInput_立即同步赛事_成功抓取一场并落草稿()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();

        RecordingBotReplySender sender = new();
        FakeProvider provider = new([Match("match-now", DateTime.UtcNow.AddDays(3))]);
        CandyBotFunctionConfig cfg = new() { System = true, Betting = true, BettingAutomation = true };
        CandyBotService service = BuildService(sql, cfg, sender, provider);

        bool handled = await service.HandleBettingInput(new C2CMessage { Detail = "立即同步赛事", Author = new() { Id = "admin" } }, AdminUser());

        handled.Should().BeTrue();
        string? reply = sender.TextContents.Single();
        reply.Should().Contain("同步完成").And.Contain("抓取 1 场").And.Contain("新增草稿 1 场");

        // 端到端验证：草稿（is_published=0）确实落库。
        DataRow? counts = sql.ExecuteDataRow("SELECT COUNT(*) AS c FROM betting_matches WHERE is_published=0");
        Convert.ToInt32(counts!["c"]).Should().Be(1);
    }
}
