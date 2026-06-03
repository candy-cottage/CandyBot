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
        DateTime start = DateTime.Now.AddDays(3);
        EsportsSyncService service = new(
            NullLogger<EsportsSyncService>.Instance,
            sql,
            new BettingService(sql, new CandyBotFunctionConfig()),
            new FakeProvider([Match("match-1", start)]),
            new PandaScoreConfig { GameSlug = "csgo" },
            taskQueue: null!);

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
        DateTime start = DateTime.Now.AddDays(3);
        EsportsSyncService service = new(
            NullLogger<EsportsSyncService>.Instance,
            sql,
            new BettingService(sql, new CandyBotFunctionConfig()),
            new FakeProvider([Match("match-2", start)]),
            new PandaScoreConfig { GameSlug = "csgo" },
            taskQueue: null!);

        await service.SyncSchedulesAsync();

        DataRow? row = sql.ExecuteDataRow("SELECT e.is_published AS event_published, m.is_published AS match_published, m.status, m.betting_enabled, m.available_options, m.bet_deadline, m.team1_win_odds, m.team2_win_odds FROM betting_matches m JOIN betting_events e ON e.id=m.event_id");
        Convert.ToInt32(row!["event_published"]).Should().Be(0);
        Convert.ToInt32(row["match_published"]).Should().Be(0);
        Convert.ToInt32(row["status"]).Should().Be(0);
        Convert.ToInt32(row["betting_enabled"]).Should().Be(1);
        row["available_options"].ToString().Should().Contain("team1_win").And.Contain("team2_win");
        Convert.ToDateTime(row["bet_deadline"]).Should().BeCloseTo(start, TimeSpan.FromSeconds(1));
        Convert.ToDecimal(row["team1_win_odds"]).Should().Be(1.85m);
        Convert.ToDecimal(row["team2_win_odds"]).Should().Be(1.85m);
    }
}
