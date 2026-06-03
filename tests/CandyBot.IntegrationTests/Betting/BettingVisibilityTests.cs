using CandyBot.IntegrationTests.Infrastructure;
using CandyCottage.CandyBot.Services;
using CandyCottage.CandyBot.Utilities;
using FluentAssertions;
using Xunit;

namespace CandyBot.IntegrationTests.Betting;

[Collection(MySqlCollection.Name)]
[Trait("Category", "MySql")]
public class BettingVisibilityTests(MySqlFixture fixture)
{
    private readonly MySqlFixture _fixture = fixture;

    [Fact]
    public async Task 用户查询_不显示未发布赛事和比赛()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();
        BettingService service = new(sql, new CandyBotFunctionConfig());

        service.CreateEvent("已发布赛事", DateTime.Now.AddDays(1), DateTime.Now.AddDays(2), out _, out long? visibleEventId).Should().BeTrue();
        service.CreateMatch((int)visibleEventId!.Value, "A", "B", "小组赛", DateTime.Now.AddDays(1), DateTime.Now.AddDays(1), "team1_win,team2_win", null, null, null, true, out _, out long? visibleMatchId).Should().BeTrue();

        sql.Execute("INSERT INTO betting_events (external_id, is_published, name, status, start_time, end_time) VALUES ('evt-hidden', 0, '草稿赛事', 0, NOW(), DATE_ADD(NOW(), INTERVAL 1 DAY))");
        long hiddenEventId = sql.LastInsertId;
        sql.Parameters["@eid"] = hiddenEventId;
        sql.Execute(@"INSERT INTO betting_matches (external_id, is_published, event_id, team1_name, team2_name, stage, status, start_time, bet_deadline, available_options, team1_win_odds, team2_win_odds, description, betting_enabled)
            VALUES ('match-hidden', 0, @eid, 'HiddenA', 'HiddenB', '草稿', 0, DATE_ADD(NOW(), INTERVAL 1 DAY), DATE_ADD(NOW(), INTERVAL 1 DAY), '[""team1_win"",""team2_win""]', 1.85, 1.85, '', 1)");
        long hiddenMatchId = sql.LastInsertId;

        service.GetEventsOverview(1).Item1.Should().Contain("已发布赛事").And.NotContain("草稿赛事");
        service.GetAllMatches(1).Item1.Should().Contain("A vs B").And.NotContain("HiddenA");
        service.GetEventDetail(hiddenEventId, 1).Item1.Should().Be("赛事不存在。");
        service.GetMatchDetail(hiddenMatchId, out _).Should().Be("比赛不存在。");
    }
}
