using System.Data;
using CandyBot.IntegrationTests.Infrastructure;
using CandyCottage.CandyBot.Services;
using CandyCottage.CandyBot.Utilities;
using FluentAssertions;
using Milimoe.FunGame.Core.Api.Utility;
using Milimoe.FunGame.Core.Entity;
using ProjectRedbud.FunGame.SQLQueryExtension;
using Xunit;

namespace CandyBot.IntegrationTests.Betting;

[Collection(MySqlCollection.Name)]
[Trait("Category", "MySql")]
public class BettingConfirmSettlementTests(MySqlFixture fixture)
{
    private readonly MySqlFixture _fixture = fixture;

    /// <summary>创建一个赛事 + 一场可结算的比赛，返回 matchId。比赛开始时间始终设在过去，
    /// 避免 UpdateStatuses 把状态重置回 0 而绕过“已结算”守卫。
    /// <paramref name="betDeadline"/> 默认在过去（比赛已截止、不可下注）；需要先下注的用例传一个未来时间。</summary>
    private long SeedMatch(BettingService service, DateTime? betDeadline = null)
    {
        service.CreateEvent("测试赛事", DateTime.Now.AddDays(-1), DateTime.Now.AddDays(1), out _, out long? eventId)
            .Should().BeTrue();
        service.CreateMatch((int)eventId!.Value, "NAVI", "FaZe", "小组赛",
            DateTime.Now.AddHours(-2), betDeadline ?? DateTime.Now.AddHours(-3),
            "team1_win,team2_win", 2m, 2m, null, true, out _, out long? matchId)
            .Should().BeTrue();
        return matchId!.Value;
    }

    /// <summary>用 FunGame SQL 扩展注册一个用户并设置初始金币，返回 uid。每次取全新 helper 并 Dispose。</summary>
    private long SeedUser(string username, double credits)
    {
        string autoKey = username.Encrypt(username);
        using MySQLHelper sql = _fixture.CreateHelper();
        sql.RegisterUser(username, "pwd", "", "", autoKey);
        User user = sql.GetUserByUsernameAndAutoKey(username, autoKey, loadInventory: true)
            ?? throw new InvalidOperationException("种子用户创建失败。");
        user.Inventory.Credits = credits;
        sql.UpdateUser(user);
        return user.Id;
    }

    /// <summary>读取用户当前金币（全新 helper）。</summary>
    private double GetCredits(long uid)
    {
        using MySQLHelper sql = _fixture.CreateHelper();
        User user = sql.GetUserById(uid, loadInventory: true)
            ?? throw new InvalidOperationException($"找不到用户 {uid}。");
        return user.Inventory.Credits;
    }

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

    [Fact]
    public async Task GetPendingSettlementMatches_列出待确认自动结果()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();
        BettingService service = new(sql, new CandyBotFunctionConfig());
        long matchId = SeedMatch(service);
        sql.Parameters["@mid"] = matchId;
        sql.Execute("UPDATE betting_matches SET result='2:1', winner=1, pending_settlement=1, betting_enabled=0 WHERE id=@mid");
        sql.Execute("INSERT INTO betting_events (name, status, start_time, end_time, is_published) VALUES ('隐藏赛事', 0, NOW(), DATE_ADD(NOW(), INTERVAL 1 DAY), 0)");
        long hiddenEventId = sql.LastInsertId;
        sql.Parameters["@eid"] = hiddenEventId;
        sql.Execute(@"INSERT INTO betting_matches (event_id, team1_name, team2_name, stage, status, start_time, bet_deadline, result, winner, available_options, team1_win_odds, team2_win_odds, betting_enabled, is_published, pending_settlement)
            VALUES (@eid, '隐藏A', '隐藏B', '小组赛', 0, DATE_SUB(NOW(), INTERVAL 2 HOUR), DATE_SUB(NOW(), INTERVAL 3 HOUR), '1:0', 1, '[""team1_win"",""team2_win""]', 2, 2, 0, 1, 1)");

        var (content, totalPages) = service.GetPendingSettlementMatches(1, pageSize: 1);

        totalPages.Should().Be(1);
        content.Should().Contain("待确认结算比赛");
        content.Should().Contain("NAVI vs FaZe");
        content.Should().Contain("2:1");
        content.Should().Contain(matchId.ToString());
        content.Should().NotContain("隐藏A");
    }

    /// <summary>
    /// 没有待确认结果（pending_settlement 默认 0）时，确认应短路返回提示，<b>不触发任何结算</b>：
    /// 比赛 status 仍为 0、pending 仍为 0。
    /// </summary>
    [Fact]
    public async Task ConfirmPendingSettlement_无待确认结果_返回提示且不结算()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();
        BettingService service = new(sql, new CandyBotFunctionConfig());
        long matchId = SeedMatch(service);
        // 不设置 pending_settlement（默认 0）

        string msg = service.ConfirmPendingSettlement((int)matchId);

        msg.Should().Be("该比赛没有待确认结算结果。");
        sql.Parameters["@mid"] = matchId;
        DataRow? row = sql.ExecuteDataRow("SELECT status, pending_settlement FROM betting_matches WHERE id=@mid");
        Convert.ToInt32(row!["status"]).Should().Be(0, "未触发结算，状态应保持未结束 0");
        Convert.ToInt32(row["pending_settlement"]).Should().Be(0);
    }

    /// <summary>
    /// 幂等：首次确认结算后 pending 标记被清零，第二次确认时守卫命中、<b>不再进入 SettleMatch</b>。
    /// 证明确认路径无法对同一场比赛重复处理。
    /// </summary>
    [Fact]
    public async Task ConfirmPendingSettlement_重复确认_第二次不再结算()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();
        BettingService service = new(sql, new CandyBotFunctionConfig());
        long matchId = SeedMatch(service);
        sql.Parameters["@mid"] = matchId;
        sql.Execute("UPDATE betting_matches SET result='2:1', winner=1, pending_settlement=1 WHERE id=@mid");

        // 第一次确认：结算完成，状态置 2、pending 清 0
        string first = service.ConfirmPendingSettlement((int)matchId);
        first.Should().Contain("结算完成");
        sql.Parameters["@mid"] = matchId;
        DataRow? afterFirst = sql.ExecuteDataRow("SELECT status, pending_settlement FROM betting_matches WHERE id=@mid");
        Convert.ToInt32(afterFirst!["status"]).Should().Be(2);
        Convert.ToInt32(afterFirst["pending_settlement"]).Should().Be(0);

        // 第二次确认：pending 已清零，守卫命中，SettleMatch 不被重入
        string second = service.ConfirmPendingSettlement((int)matchId);
        second.Should().Be("该比赛没有待确认结算结果。");
        sql.Parameters["@mid"] = matchId;
        DataRow? afterSecond = sql.ExecuteDataRow("SELECT status, pending_settlement FROM betting_matches WHERE id=@mid");
        Convert.ToInt32(afterSecond!["status"]).Should().Be(2, "已结算状态不变");
        Convert.ToInt32(afterSecond["pending_settlement"]).Should().Be(0);
    }

    /// <summary>
    /// 真正的“重复发奖”后挡板：即便有人在 status 已为 2 时把 pending 重新置回 1（重试/脏标记），
    /// 再次确认会被 SettleMatch 的“比赛已结算”守卫拦下——用户余额不变，绝不二次派彩。
    /// 直接验证 SettleMatch status=2 这一最终防线（区别于 #3 的守卫提前短路）。
    /// </summary>
    [Fact]
    public async Task ConfirmPendingSettlement_已结算后重置pending再确认_不重复发奖()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();
        BettingService service = new(sql, new CandyBotFunctionConfig());
        const double initial = 50000;
        long uid = SeedUser("u-confirm-backstop", initial);
        // 截止时间放在未来，使下注开放；开始时间仍在过去（SeedMatch 固定），保证之后可结算。
        long matchId = SeedMatch(service, betDeadline: DateTime.Now.AddDays(1));

        const long bet = 1000;
        service.PlaceBet(uid, matchId, "team1", bet, out string be).Should().BeTrue(be);
        // 模拟处理器：下注成功后扣减本金
        using (MySQLHelper debitSql = _fixture.CreateHelper())
        {
            User u = debitSql.GetUserById(uid, loadInventory: true)!;
            u.Inventory.Credits -= bet;
            debitSql.UpdateUser(u);
        }

        sql.Parameters["@mid"] = matchId;
        sql.Execute("UPDATE betting_matches SET result='2:1', winner=1, pending_settlement=1 WHERE id=@mid");

        // 首次确认：派彩。payout = 1000 * 2.0 = 2000，模拟处理器领奖发放到余额
        service.ConfirmPendingSettlement((int)matchId).Should().Contain("结算完成");
        long payout;
        using (MySQLHelper claimSql = _fixture.CreateHelper())
            payout = new BettingService(claimSql, new CandyBotFunctionConfig()).ClaimRewards(uid);
        payout.Should().Be(2000);
        using (MySQLHelper creditSql = _fixture.CreateHelper())
        {
            User u = creditSql.GetUserById(uid, loadInventory: true)!;
            u.Inventory.Credits += payout;
            creditSql.UpdateUser(u);
        }
        double balanceAfterPayout = GetCredits(uid);
        balanceAfterPayout.Should().Be(initial - bet + payout, "首次确认应正常派彩");

        // 人为把已结算（status=2）比赛的 pending 标记重置回 1，模拟陈旧/重试标记
        sql.Parameters["@mid"] = matchId;
        sql.Execute("UPDATE betting_matches SET pending_settlement=1 WHERE id=@mid");

        // 再次确认：SettleMatch 的 status=2 守卫拦下，提示“已结算”，余额不变
        string again = service.ConfirmPendingSettlement((int)matchId);
        again.Should().Contain("已结算");
        GetCredits(uid).Should().Be(balanceAfterPayout, "已结算比赛不得二次派彩");
    }
}
