using CandyBot.IntegrationTests.Infrastructure;
using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Services;
using CandyCottage.CandyBot.Services.BotServices;
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
public class BettingDraftPublishTests(MySqlFixture fixture)
{
    private readonly MySqlFixture _fixture = fixture;

    private sealed class RecordingBotReplySender : IBotReplySender
    {
        public List<BotReply> Sent { get; } = [];
        public IEnumerable<string?> TextContents => Sent.Select(reply => reply.Text ?? reply.Markdown?.Content);

        public Task SendAsync(IBotMessage msg, BotReply reply, int? msgSeq = null)
        {
            Sent.Add(reply);
            return Task.CompletedTask;
        }
    }

    private static User AdminUser()
    {
        User user = Factory.GetUser();
        user.Id = 1;
        user.Username = "admin";
        user.IsAdmin = true;
        return user;
    }

    [Fact]
    public async Task PublishMatch_发布比赛时连带发布父赛事()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();
        sql.Execute("INSERT INTO betting_events (external_id, is_published, name, status, start_time, end_time) VALUES ('evt-1', 0, '草稿赛事', 0, NOW(), DATE_ADD(NOW(), INTERVAL 1 DAY))");
        long eventId = sql.LastInsertId;
        sql.Parameters["@eid"] = eventId;
        sql.Execute(@"INSERT INTO betting_matches (external_id, is_published, event_id, team1_name, team2_name, stage, status, start_time, bet_deadline, available_options, team1_win_odds, team2_win_odds, description, betting_enabled)
            VALUES ('match-1', 0, @eid, 'NAVI', 'FaZe', '小组赛', 0, DATE_ADD(NOW(), INTERVAL 1 DAY), DATE_ADD(NOW(), INTERVAL 1 DAY), '[""team1_win"",""team2_win""]', 1.85, 1.85, '', 1)");
        long matchId = sql.LastInsertId;

        BettingService service = new(sql, new CandyBotFunctionConfig());
        service.PublishMatch((int)matchId, out string error).Should().BeTrue(error);

        service.GetEventsOverview(1).Item1.Should().Contain("草稿赛事");
        service.GetAllMatches(1).Item1.Should().Contain("NAVI vs FaZe");
    }

    [Fact]
    public async Task HandleBettingInput_待发布比赛_管理员收到草稿列表()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();
        sql.Execute("INSERT INTO betting_events (external_id, is_published, name, status, start_time, end_time) VALUES ('evt-list', 0, '待发布赛事', 0, NOW(), DATE_ADD(NOW(), INTERVAL 1 DAY))");
        long eventId = sql.LastInsertId;
        sql.Parameters["@eid"] = eventId;
        sql.Execute(@"INSERT INTO betting_matches (external_id, is_published, event_id, team1_name, team2_name, stage, status, start_time, bet_deadline, available_options, team1_win_odds, team2_win_odds, description, betting_enabled)
            VALUES ('match-list', 0, @eid, 'NAVI', 'FaZe', '小组赛', 0, DATE_ADD(NOW(), INTERVAL 1 DAY), DATE_ADD(NOW(), INTERVAL 1 DAY), '[""team1_win"",""team2_win""]', 1.85, 1.85, '', 1)");

        RecordingBotReplySender sender = new();
        CandyBotFunctionConfig cfg = new() { System = true, Betting = true, BettingAutomation = true };
        CandyBotService service = new(
            NullLogger<CandyBotService>.Instance,
            new MemoryCache(new MemoryCacheOptions()),
            sql,
            cfg,
            taskQueue: null!,
            sender,
            new BettingService(sql, cfg),
            sqlInitService: null!,
            userService: null!,
            forzaService: null!,
            forzaSheetImporter: null!,
            esportsSyncService: null!);

        bool handled = await service.HandleBettingInput(new C2CMessage { Detail = "待发布比赛", Author = new() { Id = "admin" } }, AdminUser());

        handled.Should().BeTrue();
        sender.TextContents.Single().Should().Contain("NAVI vs FaZe");
    }
}
