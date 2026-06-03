using System.Data;
using CandyBot.IntegrationTests.Infrastructure;
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
public class EsportsScheduleSyncTests(MySqlFixture fixture)
{
    private readonly MySqlFixture _fixture = fixture;

    private sealed class FakeProvider(IReadOnlyList<ExternalEsportsMatch> matches) : IEsportsDataProvider
    {
        public Task<IReadOnlyList<ExternalEsportsMatch>> GetUpcomingMatchesAsync(string gameSlug, DateTime since, CancellationToken cancellationToken = default)
            => Task.FromResult(matches);

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

    [Fact]
    public async Task SyncSchedulesAsync_同一批重复同步不产生重复赛事或比赛()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();
        DateTime start = DateTime.UtcNow.AddDays(3);
        EsportsSyncService service = new(
            NullLogger<EsportsSyncService>.Instance,
            sql,
            new BettingService(sql, new CandyBotFunctionConfig()),
            new FakeProvider([Match("match-1", start)]),
            new PandaScoreConfig { GameSlug = "csgo" });

        EsportsScheduleSyncReport first = await service.SyncSchedulesAsync();
        EsportsScheduleSyncReport second = await service.SyncSchedulesAsync();

        first.MatchesCreated.Should().Be(1);
        second.MatchesCreated.Should().Be(0);
        second.MatchesUpdated.Should().Be(1);

        DataRow? counts = sql.ExecuteDataRow("SELECT (SELECT COUNT(*) FROM betting_events) AS events_count, (SELECT COUNT(*) FROM betting_matches) AS matches_count");
        Convert.ToInt32(counts!["events_count"]).Should().Be(1);
        Convert.ToInt32(counts["matches_count"]).Should().Be(1);
    }

    [Fact]
    public async Task SyncSchedulesAsync_新比赛入草稿且默认只开放胜负选项()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();
        DateTime start = DateTime.UtcNow.AddDays(3);
        DateTime beijingStart = ChinaStandardTime.FromUtc(start);
        EsportsSyncService service = new(
            NullLogger<EsportsSyncService>.Instance,
            sql,
            new BettingService(sql, new CandyBotFunctionConfig()),
            new FakeProvider([Match("match-2", start)]),
            new PandaScoreConfig { GameSlug = "csgo" });

        await service.SyncSchedulesAsync();

        DataRow? row = sql.ExecuteDataRow("SELECT e.is_published AS event_published, m.is_published AS match_published, m.status, m.betting_enabled, m.available_options, m.bet_deadline, m.team1_win_odds, m.team2_win_odds FROM betting_matches m JOIN betting_events e ON e.id=m.event_id");
        Convert.ToInt32(row!["event_published"]).Should().Be(0);
        Convert.ToInt32(row["match_published"]).Should().Be(0);
        Convert.ToInt32(row["status"]).Should().Be(0);
        Convert.ToInt32(row["betting_enabled"]).Should().Be(1);
        row["available_options"].ToString().Should().Contain("team1_win").And.Contain("team2_win");
        Convert.ToDateTime(row["bet_deadline"]).Should().BeCloseTo(beijingStart, TimeSpan.FromSeconds(1));
        Convert.ToDecimal(row["team1_win_odds"]).Should().Be(1.85m);
        Convert.ToDecimal(row["team2_win_odds"]).Should().Be(1.85m);
    }

    [Fact]
    public async Task SyncSchedulesAsync_UTC开赛时间在写库边界转为北京时间()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();

        // provider 产出的 StartTime 为 UTC（Kind=Utc）；写库边界统一转为北京时间，
        // 避免部署服务器时区影响用户看到的赛程与赛事状态窗口。
        DateTime utcStart = DateTime.SpecifyKind(new DateTime(2026, 6, 10, 18, 0, 0), DateTimeKind.Utc);
        DateTime beijingStart = new(2026, 6, 11, 2, 0, 0);
        EsportsSyncService service = new(
            NullLogger<EsportsSyncService>.Instance,
            sql,
            new BettingService(sql, new CandyBotFunctionConfig()),
            new FakeProvider([Match("match-utc", utcStart)]),
            new PandaScoreConfig { GameSlug = "csgo" });

        await service.SyncSchedulesAsync();

        DataRow? row = sql.ExecuteDataRow("SELECT start_time, bet_deadline FROM betting_matches WHERE external_id='match-utc'");
        Convert.ToDateTime(row!["start_time"]).Should().BeCloseTo(beijingStart, TimeSpan.FromSeconds(1));
        Convert.ToDateTime(row["bet_deadline"]).Should().BeCloseTo(beijingStart, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task SyncSchedulesAsync_已发布比赛重新同步_仅刷新开赛时间且不回退发布状态()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();

        // 预置一个已发布赛事（external_id 与 FakeProvider 的锚点一致），赛事 UPDATE 带 AND is_published=0，应被原样保留。
        DateTime eventStart = ChinaStandardTime.Now.AddDays(1);
        sql.Parameters["@start"] = eventStart;
        sql.Parameters["@end"] = eventStart.AddDays(7);
        sql.Execute("INSERT INTO betting_events (external_id, is_published, name, status, start_time, end_time) VALUES ('league:season:tournament', 1, '已发布赛事', 0, @start, @end)");
        long eventId = sql.LastInsertId;

        // 预置一个已发布、未开始的比赛，初始开赛/截止时间为 now+2d，并使用可辨识的队名以验证刷新分支不覆盖阵容。
        DateTime initialStart = ChinaStandardTime.Now.AddDays(2);
        sql.Parameters["@eid"] = eventId;
        sql.Parameters["@start"] = initialStart;
        sql.Parameters["@deadline"] = initialStart;
        sql.Execute(@"INSERT INTO betting_matches
            (external_id, is_published, pending_settlement, event_id, team1_name, team2_name, stage, start_time, bet_deadline,
             available_options, team1_win_odds, team2_win_odds, description, status, betting_enabled)
            VALUES ('match-pub', 1, 0, @eid, 'OldTeam1', 'OldTeam2', '小组赛', @start, @deadline,
             '[""team1_win"",""team2_win""]', 2.00, 2.00, '', 0, 1)");
        long matchId = sql.LastInsertId;

        DateTime newStart = DateTime.UtcNow.AddDays(5);
        DateTime newStartBeijing = ChinaStandardTime.FromUtc(newStart);
        EsportsSyncService service = new(
            NullLogger<EsportsSyncService>.Instance,
            sql,
            new BettingService(sql, new CandyBotFunctionConfig()),
            new FakeProvider([Match("match-pub", newStart)]),
            new PandaScoreConfig { GameSlug = "csgo" });

        await service.SyncSchedulesAsync();

        sql.Parameters["@mid"] = matchId;
        DataRow? match = sql.ExecuteDataRow("SELECT start_time, bet_deadline, is_published, status, team1_name, team2_name FROM betting_matches WHERE id=@mid");
        Convert.ToDateTime(match!["start_time"]).Should().BeCloseTo(newStartBeijing, TimeSpan.FromSeconds(1));
        Convert.ToDateTime(match["bet_deadline"]).Should().BeCloseTo(newStartBeijing, TimeSpan.FromSeconds(1));
        Convert.ToInt32(match["is_published"]).Should().Be(1);
        Convert.ToInt32(match["status"]).Should().Be(0);
        match["team1_name"].ToString().Should().Be("OldTeam1");
        match["team2_name"].ToString().Should().Be("OldTeam2");

        sql.Parameters["@eid"] = eventId;
        DataRow? evt = sql.ExecuteDataRow("SELECT is_published FROM betting_events WHERE id=@eid");
        Convert.ToInt32(evt!["is_published"]).Should().Be(1);
    }
}
