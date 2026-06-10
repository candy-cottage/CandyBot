using System.Data;
using CandyBot.IntegrationTests.Infrastructure;
using CandyCottage.CandyBot.Interfaces;
using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Services;
using CandyCottage.CandyBot.Services.Esports;
using CandyCottage.CandyBot.Utilities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CandyBot.IntegrationTests.Betting;

[Collection(MySqlCollection.Name)]
[Trait("Category", "MySql")]
public class EsportsResultSyncTests(MySqlFixture fixture)
{
    private readonly MySqlFixture _fixture = fixture;

    private sealed class FakeProvider(IReadOnlyList<ExternalEsportsResult> results) : IEsportsDataProvider
    {
        public Task<IReadOnlyList<ExternalEsportsMatch>> GetUpcomingMatchesAsync(string gameSlug, DateTime since, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ExternalEsportsMatch>>([]);

        public Task<IReadOnlyList<ExternalEsportsResult>> GetFinishedResultsAsync(IReadOnlyCollection<string> externalIds, CancellationToken cancellationToken = default)
            => Task.FromResult(results.Where(r => externalIds.Contains(r.ExternalId)).ToArray() as IReadOnlyList<ExternalEsportsResult>);
    }

    [Fact]
    public async Task SyncResultsAsync_已发布胜负比赛预填结果并标记待确认()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();
        BettingService betting = new(sql, new CandyBotFunctionConfig());
        betting.CreateEvent("测试赛事", ChinaStandardTime.Now.AddDays(-1), ChinaStandardTime.Now.AddDays(1), out _, out long? eventId).Should().BeTrue();
        sql.Parameters["@eid"] = eventId!.Value;
        sql.Execute(@"INSERT INTO betting_matches (external_id, is_published, pending_settlement, event_id, team1_name, team2_name, stage, status, start_time, bet_deadline, available_options, team1_win_odds, team2_win_odds, description, betting_enabled)
            VALUES ('match-1', 1, 0, @eid, 'NAVI', 'FaZe', '小组赛', 1, DATE_SUB(NOW(), INTERVAL 2 HOUR), DATE_SUB(NOW(), INTERVAL 3 HOUR), '[""team1_win"",""team2_win""]', 1.85, 1.85, '', 1)");
        long matchId = sql.LastInsertId;

        EsportsSyncService service = new(NullLogger<EsportsSyncService>.Instance, sql, betting,
            new FakeProvider([new ExternalEsportsResult("match-1", "2:1", 1)]),
            new PandaScoreConfig { GameSlug = "csgo" });

        EsportsResultSyncReport report = await service.SyncResultsAsync();

        report.PendingSettlementCreated.Should().Be(1);
        sql.Parameters["@mid"] = matchId;
        DataRow? row = sql.ExecuteDataRow("SELECT result, winner, pending_settlement, status FROM betting_matches WHERE id=@mid");
        row!["result"].Should().Be("2:1");
        Convert.ToInt32(row["winner"]).Should().Be(1);
        Convert.ToInt32(row["pending_settlement"]).Should().Be(1);
        Convert.ToInt32(row["status"]).Should().Be(1, "sync must not settle money or mark final status");
    }

    [Fact]
    public async Task SyncResultsAsync_比分或MVP比赛_计入待人工处理且不预填()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();
        BettingService betting = new(sql, new CandyBotFunctionConfig());
        betting.CreateEvent("测试赛事", ChinaStandardTime.Now.AddDays(-1), ChinaStandardTime.Now.AddDays(1), out _, out long? eventId).Should().BeTrue();
        sql.Parameters["@eid"] = eventId!.Value;
        // 含 score 选项的比赛无法仅凭 winner 自动判定，必须归入待人工结算而非预填。
        sql.Execute(@"INSERT INTO betting_matches (external_id, is_published, pending_settlement, event_id, team1_name, team2_name, stage, status, start_time, bet_deadline, available_options, team1_win_odds, team2_win_odds, description, betting_enabled)
            VALUES ('match-score', 1, 0, @eid, 'NAVI', 'FaZe', '小组赛', 1, DATE_SUB(NOW(), INTERVAL 2 HOUR), DATE_SUB(NOW(), INTERVAL 3 HOUR), '[""score""]', 1.85, 1.85, '', 1)");
        long matchId = sql.LastInsertId;

        EsportsSyncService service = new(NullLogger<EsportsSyncService>.Instance, sql, betting,
            new FakeProvider([new ExternalEsportsResult("match-score", "16:14", 1)]),
            new PandaScoreConfig { GameSlug = "csgo" });

        EsportsResultSyncReport report = await service.SyncResultsAsync();

        report.RequiresManualSettlement.Should().Be(1);
        report.PendingSettlementCreated.Should().Be(0);

        sql.Parameters["@mid"] = matchId;
        DataRow? row = sql.ExecuteDataRow("SELECT result, winner, pending_settlement FROM betting_matches WHERE id=@mid");
        Convert.ToInt32(row!["pending_settlement"]).Should().Be(0, "score/mvp match must be deferred, not prefilled");
        row["result"].Should().Be(DBNull.Value, "deferred match must not have its result prefilled");
        row["winner"].Should().Be(DBNull.Value, "deferred match must not have its winner prefilled");
    }

    [Fact]
    public async Task SyncResultsAsync_重复运行_已待确认比赛不重复预填()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();
        BettingService betting = new(sql, new CandyBotFunctionConfig());
        betting.CreateEvent("测试赛事", ChinaStandardTime.Now.AddDays(-1), ChinaStandardTime.Now.AddDays(1), out _, out long? eventId).Should().BeTrue();
        sql.Parameters["@eid"] = eventId!.Value;
        sql.Execute(@"INSERT INTO betting_matches (external_id, is_published, pending_settlement, event_id, team1_name, team2_name, stage, status, start_time, bet_deadline, available_options, team1_win_odds, team2_win_odds, description, betting_enabled)
            VALUES ('match-idem', 1, 0, @eid, 'NAVI', 'FaZe', '小组赛', 1, DATE_SUB(NOW(), INTERVAL 2 HOUR), DATE_SUB(NOW(), INTERVAL 3 HOUR), '[""team1_win"",""team2_win""]', 1.85, 1.85, '', 1)");
        long matchId = sql.LastInsertId;

        EsportsSyncService service = new(NullLogger<EsportsSyncService>.Instance, sql, betting,
            new FakeProvider([new ExternalEsportsResult("match-idem", "2:1", 1)]),
            new PandaScoreConfig { GameSlug = "csgo" });

        EsportsResultSyncReport first = await service.SyncResultsAsync();
        first.PendingSettlementCreated.Should().Be(1);

        // 第二次运行：候选过滤 pending_settlement=0 已把这场排除，因此它根本不再是候选。
        EsportsResultSyncReport second = await service.SyncResultsAsync();
        second.Checked.Should().Be(0, "the now-pending match is excluded by the pending_settlement=0 candidate filter");
        second.PendingSettlementCreated.Should().Be(0, "an already-pending match must not be re-prefilled");

        sql.Parameters["@mid"] = matchId;
        DataRow? row = sql.ExecuteDataRow("SELECT result, winner, pending_settlement FROM betting_matches WHERE id=@mid");
        row!["result"].Should().Be("2:1");
        Convert.ToInt32(row["winner"]).Should().Be(1);
        Convert.ToInt32(row["pending_settlement"]).Should().Be(1);
    }

    [Fact]
    public async Task SyncResultsAsync_已取消比赛不预填结果()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();
        BettingService betting = new(sql, new CandyBotFunctionConfig());
        betting.CreateEvent("测试赛事", ChinaStandardTime.Now.AddDays(-1), ChinaStandardTime.Now.AddDays(1), out _, out long? eventId).Should().BeTrue();
        sql.Parameters["@eid"] = eventId!.Value;
        sql.Execute(@"INSERT INTO betting_matches (external_id, is_published, pending_settlement, event_id, team1_name, team2_name, stage, status, start_time, bet_deadline, available_options, team1_win_odds, team2_win_odds, description, betting_enabled)
            VALUES ('match-canceled', 1, 0, @eid, 'NAVI', 'FaZe', '小组赛', 3, DATE_SUB(NOW(), INTERVAL 2 HOUR), DATE_SUB(NOW(), INTERVAL 3 HOUR), '[""team1_win"",""team2_win""]', 1.85, 1.85, '', 0)");
        long matchId = sql.LastInsertId;

        EsportsSyncService service = new(NullLogger<EsportsSyncService>.Instance, sql, betting,
            new FakeProvider([new ExternalEsportsResult("match-canceled", "2:1", 1)]),
            new PandaScoreConfig { GameSlug = "csgo" });

        EsportsResultSyncReport report = await service.SyncResultsAsync();

        report.Checked.Should().Be(0, "取消的比赛不应进入结果同步候选集");
        report.PendingSettlementCreated.Should().Be(0);
        sql.Parameters["@mid"] = matchId;
        DataRow? row = sql.ExecuteDataRow("SELECT result, winner, pending_settlement, status FROM betting_matches WHERE id=@mid");
        row!["result"].Should().Be(DBNull.Value);
        row["winner"].Should().Be(DBNull.Value);
        Convert.ToInt32(row["pending_settlement"]).Should().Be(0);
        Convert.ToInt32(row["status"]).Should().Be(3);
    }
}
