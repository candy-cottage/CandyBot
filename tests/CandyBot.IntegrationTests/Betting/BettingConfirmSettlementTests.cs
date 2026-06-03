using System.Data;
using CandyBot.IntegrationTests.Infrastructure;
using CandyCottage.CandyBot.Services;
using CandyCottage.CandyBot.Utilities;
using FluentAssertions;
using Xunit;

namespace CandyBot.IntegrationTests.Betting;

[Collection(MySqlCollection.Name)]
[Trait("Category", "MySql")]
public class BettingConfirmSettlementTests(MySqlFixture fixture)
{
    private readonly MySqlFixture _fixture = fixture;

    [Fact]
    public async Task ConfirmPendingSettlement_调用现有SettleMatch并清理pending标记()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();
        BettingService service = new(sql, new CandyBotFunctionConfig());
        service.CreateEvent("测试赛事", DateTime.Now.AddDays(-1), DateTime.Now.AddDays(1), out _, out long? eventId).Should().BeTrue();
        service.CreateMatch((int)eventId!.Value, "NAVI", "FaZe", "小组赛", DateTime.Now.AddHours(-2), DateTime.Now.AddHours(-3),
            "team1_win,team2_win", 2m, 2m, null, true, out _, out long? matchId).Should().BeTrue();
        sql.Parameters["@mid"] = matchId!.Value;
        sql.Execute("UPDATE betting_matches SET result='2:1', winner=1, pending_settlement=1 WHERE id=@mid");

        string msg = service.ConfirmPendingSettlement((int)matchId.Value);

        msg.Should().Contain("结算完成");
        sql.Parameters["@mid"] = matchId.Value;
        DataRow? row = sql.ExecuteDataRow("SELECT status, pending_settlement FROM betting_matches WHERE id=@mid");
        Convert.ToInt32(row!["status"]).Should().Be(2);
        Convert.ToInt32(row["pending_settlement"]).Should().Be(0);
    }
}
