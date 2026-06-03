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

        // 资金/作弊相关：草稿（未发布）比赛即便 id 已知，也必须被视为不存在，
        // 禁止下注与查询开赛/截止时间。PlaceBet 与 GetMatchTimes 都在用户校验之前
        // 以 is_published = 1 过滤命中“比赛不存在。”，因此无需种子用户即可断言。

        // 反向控制：已发布比赛的 GetMatchTimes 必须成功，证明过滤区分了“已发布 vs 草稿”，而非一律失败。
        service.GetMatchTimes(visibleMatchId!.Value, out _, out _, out _, out string visibleErr)
            .Should().BeTrue(visibleErr);

        // 草稿比赛：GetMatchTimes 返回 false 且报“比赛不存在。”（无需用户）。
        service.GetMatchTimes(hiddenMatchId, out _, out _, out _, out string hiddenTimesErr)
            .Should().BeFalse();
        hiddenTimesErr.Should().Be("比赛不存在。");

        // 草稿比赛：PlaceBet 在校验用户之前即以未发布过滤拒绝下注。
        service.PlaceBet(uid: 1, hiddenMatchId, "team1", 1000, out string hiddenBetErr)
            .Should().BeFalse();
        hiddenBetErr.Should().Be("比赛不存在。");
    }
}
