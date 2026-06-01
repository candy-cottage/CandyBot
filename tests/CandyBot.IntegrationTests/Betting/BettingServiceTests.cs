using System.Data;
using CandyBot.IntegrationTests.Infrastructure;
using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Services;
using CandyCottage.CandyBot.Utilities;
using FluentAssertions;
using Milimoe.FunGame.Core.Api.Utility;
using Milimoe.FunGame.Core.Entity;
using ProjectRedbud.FunGame.SQLQueryExtension;
using Xunit;

namespace CandyBot.IntegrationTests.Betting;

/// <summary>
/// 针对 <see cref="BettingService"/> 资金路径的真实 MySQL 集成测试（B07 下注 / B08 结算-取消-领奖）。
///
/// <para><b>关键事实（来自阅读 Services/BettingService.cs 与 BettingInputHandler.cs，而非计划假设）：</b></para>
/// <list type="bullet">
///   <item><b>金币的扣减/发放发生在 <c>BettingInputHandler</c>，而非 <see cref="BettingService"/>。</b>
///   <see cref="BettingService.PlaceBet"/> 只校验并写入下注记录，不动用户余额；
///   下注成功后由处理器执行 <c>user.Inventory.Credits -= amt</c>。
///   <see cref="BettingService.ClaimRewards"/> 返回应发金额，由处理器加到余额上。
///   因此 service 层测试断言的是“下注记录 / payout / is_settled / is_claimed”这些资金状态字段，
///   外加模拟处理器扣减后再用 <see cref="BettingService.ClaimRewards"/> 验证净额，断言精确余额变化。</item>
///   <item><b>最低下注 1000 在处理器（<c>amt &lt; 1000</c>）校验，service 层无此限制。</b>
///   故不在 service 层断言最低额，并在注释中标注。</item>
///   <item><b>金币不足在处理器校验</b>（<c>user.Inventory.Credits &lt; amt</c>），service 层不校验。</item>
///   <item><b>赛事只读（Betting_ReadOnly）在处理器 <c>预测</c> 分支前置判断</b>，
///   <see cref="BettingService.PlaceBet"/> 不读取该开关。故按计划要求不在 service 层伪造只读路径。</item>
///   <item><b>单场助力上限 10000</b> 由 <see cref="BettingService.PlaceBet"/> 在 service 层强制（<c>totalBet &gt; 10000</c>），可在此测。</item>
/// </list>
/// </summary>
[Collection(MySqlCollection.Name)]
[Trait("Category", "MySql")]
public class BettingServiceTests(MySqlFixture fixture)
{
    private readonly MySqlFixture _fixture = fixture;

    private static readonly DateTime FixedNow = new(2026, 6, 1, 12, 0, 0);

    // ---- 种子辅助 ----

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

    /// <summary>创建一个赛事 + 一场比赛，返回 matchId。betDeadline 默认在未来、可下注、team1/team2 选项。</summary>
    private long SeedMatch(
        BettingService service,
        string options = "team1_win,team2_win",
        decimal? team1Odds = 2.0m,
        decimal? team2Odds = 3.0m,
        bool enableBet = true,
        DateTime? betDeadline = null,
        DateTime? startTime = null)
    {
        // 赛事窗口设得足够宽，避免 UpdateStatuses 把它推进到已结束。
        service.CreateEvent("测试赛事", FixedNow.AddDays(-1), FixedNow.AddDays(30), out string evtErr, out long? eventId)
            .Should().BeTrue(evtErr);
        eventId.Should().NotBeNull();

        DateTime start = startTime ?? FixedNow.AddDays(1);
        DateTime ddl = betDeadline ?? FixedNow.AddDays(1);
        bool ok = service.CreateMatch(
            (int)eventId!.Value, "队伍A", "队伍B", "小组赛",
            start, ddl, options, team1Odds, team2Odds, null, enableBet,
            out string matchErr, out long? matchId);
        ok.Should().BeTrue(matchErr);
        matchId.Should().NotBeNull();
        return matchId!.Value;
    }

    private DataRow QueryBetRecord(long uid, long matchId)
    {
        using MySQLHelper sql = _fixture.CreateHelper();
        sql.Parameters["@uid"] = uid;
        sql.Parameters["@mid"] = matchId;
        sql.ExecuteDataSet("SELECT * FROM betting_bet_records WHERE user_id=@uid AND match_id=@mid ORDER BY id");
        sql.Success.Should().BeTrue();
        sql.DataSet.Tables[0].Rows.Count.Should().BeGreaterThan(0, "应已写入下注记录");
        return sql.DataSet.Tables[0].Rows[0];
    }

    // =========================================================================
    // B07 — PlaceBet 失败路径
    // =========================================================================

    [Fact]
    public async Task PlaceBet_无效选项被拒绝()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();
        BettingService service = new(sql, new CandyBotFunctionConfig());
        long uid = SeedUser("u-badopt", 50000);
        long mid = SeedMatch(service, options: "team1_win,team2_win");

        // score 选项未在 available_options 中
        bool ok = service.PlaceBet(uid, mid, "score:2:0", 1000, out string error);

        ok.Should().BeFalse();
        error.Should().Be("无效的预测选项。");
        // 不应写入任何记录
        using MySQLHelper probe = _fixture.CreateHelper();
        probe.Parameters["@uid"] = uid;
        probe.ExecuteDataSet("SELECT COUNT(*) AS c FROM betting_bet_records WHERE user_id=@uid");
        Convert.ToInt64(probe.DataSet.Tables[0].Rows[0]["c"]).Should().Be(0);
    }

    [Fact]
    public async Task PlaceBet_比赛不开放预测被拒绝()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();
        BettingService service = new(sql, new CandyBotFunctionConfig());
        long uid = SeedUser("u-disabled", 50000);
        long mid = SeedMatch(service, enableBet: false);

        bool ok = service.PlaceBet(uid, mid, "team1", 1000, out string error);

        ok.Should().BeFalse();
        error.Should().Be("该比赛不开放预测。");
    }

    [Fact]
    public async Task PlaceBet_截止时间已过被拒绝()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();
        BettingService service = new(sql, new CandyBotFunctionConfig());
        long uid = SeedUser("u-closed", 50000);
        // 截止时间设在过去：DateTime.Now > deadline 成立
        long mid = SeedMatch(service, betDeadline: DateTime.Now.AddMinutes(-5), startTime: DateTime.Now.AddDays(1));

        bool ok = service.PlaceBet(uid, mid, "team1", 1000, out string error);

        ok.Should().BeFalse();
        error.Should().Be("该比赛处于不可预测阶段。");
    }

    [Fact]
    public async Task PlaceBet_单场助力总额超过上限被拒绝()
    {
        // 注意：上限 10000 在 service 层（totalBet > 10000）。最低额 1000 在处理器层，service 不校验。
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();
        BettingService service = new(sql, new CandyBotFunctionConfig());
        long uid = SeedUser("u-cap", 50000);
        long mid = SeedMatch(service);

        // 先合法下注 8000（累计 8000）
        service.PlaceBet(uid, mid, "team1", 8000, out string e1).Should().BeTrue(e1);
        // 再下 2001 → 累计 10001 > 10000，被拒
        bool ok = service.PlaceBet(uid, mid, "team1", 2001, out string error);

        ok.Should().BeFalse();
        error.Should().Be($"本场比赛你的助力总额不能超过 10000 {Milimoe.FunGame.Core.Library.Constant.General.GameplayEquilibriumConstant.InGameCurrency}（已助力 8000）。");

        // 恰好到 10000 应允许（8000 + 2000）
        service.PlaceBet(uid, mid, "team1", 2000, out string e3).Should().BeTrue(e3);
    }

    [Fact]
    public async Task PlaceBet_比赛不存在被拒绝()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();
        BettingService service = new(sql, new CandyBotFunctionConfig());
        long uid = SeedUser("u-nomatch", 50000);

        bool ok = service.PlaceBet(uid, 999999, "team1", 1000, out string error);

        ok.Should().BeFalse();
        error.Should().Be("比赛不存在。");
    }

    // =========================================================================
    // B07 — PlaceBet 成功 + odds-at-bet-time
    // =========================================================================

    [Fact]
    public async Task PlaceBet_成功写入记录且记录下注时赔率()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();
        BettingService service = new(sql, new CandyBotFunctionConfig());
        long uid = SeedUser("u-place", 50000);
        long mid = SeedMatch(service, team1Odds: 2.0m, team2Odds: 3.0m);

        bool ok = service.PlaceBet(uid, mid, "team1", 1500, out string error);

        ok.Should().BeTrue(error);
        DataRow rec = QueryBetRecord(uid, mid);
        Convert.ToInt64(rec["amount"]).Should().Be(1500);
        Convert.ToInt32(rec["option_type"]).Should().Be(1, "team1 → option_type 1");
        Convert.ToDecimal(rec["odds_at_bet"]).Should().Be(2.0m, "应记录下注时 team1 赔率");
        Convert.ToInt32(rec["is_settled"]).Should().Be(0);
        Convert.ToInt32(rec["is_claimed"]).Should().Be(0);
    }

    [Fact]
    public async Task PlaceBet_修改赔率后已存在的下注仍按下注时赔率结算()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();
        BettingService service = new(sql, new CandyBotFunctionConfig());
        long uid = SeedUser("u-frozen-odds", 50000);
        long mid = SeedMatch(service, team1Odds: 2.0m, team2Odds: 3.0m);

        // 以 2.0 赔率下注 team1
        service.PlaceBet(uid, mid, "team1", 1000, out string e1).Should().BeTrue(e1);

        // 管理员将 team1 赔率改为 5.0
        UpdateMatchRequest req = new() { MatchId = (int)mid, Team1WinOdds = 5.0m };
        // UpdateMatch 用独立 helper（它内部开/提交事务，避免与共享 helper 状态纠缠）
        using (MySQLHelper updateSql = _fixture.CreateHelper())
        {
            BettingService updater = new(updateSql, new CandyBotFunctionConfig());
            updater.UpdateMatch(req, out string ue).Should().BeTrue(ue);
        }

        // 已存在记录的 odds_at_bet 仍为 2.0
        DataRow rec = QueryBetRecord(uid, mid);
        Convert.ToDecimal(rec["odds_at_bet"]).Should().Be(2.0m, "改赔率不应影响已下注记录");

        // 结算 team1 胜：payout 应按下注时 2.0 计算 = 1000 * 2.0 = 2000，而非新赔率 5.0
        using MySQLHelper settleSql = _fixture.CreateHelper();
        BettingService settler = new(settleSql, new CandyBotFunctionConfig());
        settler.SettleMatch((int)mid, "team1", "2:0").Should().Contain("结算完成");

        DataRow settled = QueryBetRecord(uid, mid);
        Convert.ToInt64(settled["payout"]).Should().Be(2000, "派彩应使用下注时赔率 2.0，而非更新后的 5.0");
    }

    // =========================================================================
    // B08 — 结算（赢家按下注时赔率派彩，输家 payout=0）
    // =========================================================================

    [Fact]
    public async Task SettleMatch_赢家按下注时赔率派彩_输家派彩为零()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();
        BettingService service = new(sql, new CandyBotFunctionConfig());
        long winnerUid = SeedUser("u-winner", 50000);
        long loserUid = SeedUser("u-loser", 50000);
        long mid = SeedMatch(service, team1Odds: 2.5m, team2Odds: 3.0m);

        // winnerUid 押 team1，loserUid 押 team2
        service.PlaceBet(winnerUid, mid, "team1", 1000, out string ew).Should().BeTrue(ew);
        service.PlaceBet(loserUid, mid, "team2", 2000, out string el).Should().BeTrue(el);

        // 结算 team1 胜
        string result = service.SettleMatch((int)mid, "team1", "16:14");
        result.Should().Be($"比赛 {mid} 结算完成。");

        // 赢家 payout = 1000 * 2.5 = 2500，标记中奖
        DataRow win = QueryBetRecord(winnerUid, mid);
        Convert.ToInt64(win["payout"]).Should().Be(2500);
        Convert.ToInt32(win["is_settled"]).Should().Be(1);
        win["result_note"].ToString().Should().Be("中奖");

        // 输家 payout = 0，标记未中奖
        DataRow lose = QueryBetRecord(loserUid, mid);
        Convert.ToInt64(lose["payout"]).Should().Be(0);
        Convert.ToInt32(lose["is_settled"]).Should().Be(1);
        lose["result_note"].ToString().Should().Be("未中奖");
    }

    [Fact]
    public async Task SettleMatch_已结算无法再次结算()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();
        BettingService service = new(sql, new CandyBotFunctionConfig());
        long uid = SeedUser("u-resettle", 50000);
        // start_time 设在过去：否则 UpdateStatuses 会把 start_time>=now 的比赛状态重置为 0，
        // 从而无法触发“已结算”守卫（这是 UpdateStatuses 的既有行为，非本测试关注点）。
        long mid = SeedMatch(service, startTime: DateTime.Now.AddDays(-1), betDeadline: DateTime.Now.AddDays(1));
        service.PlaceBet(uid, mid, "team1", 1000, out _).Should().BeTrue();

        using (MySQLHelper s1 = _fixture.CreateHelper())
        {
            new BettingService(s1, new CandyBotFunctionConfig())
                .SettleMatch((int)mid, "team1", "2:0").Should().Contain("结算完成");
        }
        using MySQLHelper s2 = _fixture.CreateHelper();
        new BettingService(s2, new CandyBotFunctionConfig())
            .SettleMatch((int)mid, "team1", "2:0").Should().Be("比赛已结算，无法再次结算。");
    }

    // =========================================================================
    // B08 — 取消退款（退还本金到下注记录 payout）
    // =========================================================================

    [Fact]
    public async Task CancelMatch_退还未结算助力本金()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();
        BettingService service = new(sql, new CandyBotFunctionConfig());
        long uid = SeedUser("u-cancel", 50000);
        long mid = SeedMatch(service, team1Odds: 2.0m);
        service.PlaceBet(uid, mid, "team1", 3000, out string be).Should().BeTrue(be);

        using MySQLHelper cancelSql = _fixture.CreateHelper();
        BettingService canceller = new(cancelSql, new CandyBotFunctionConfig());
        bool ok = canceller.CancelMatch((int)mid, out string msg);

        ok.Should().BeTrue(msg);
        DataRow rec = QueryBetRecord(uid, mid);
        Convert.ToInt64(rec["payout"]).Should().Be(3000, "取消应退还本金 3000（payout=amount）");
        Convert.ToInt32(rec["is_settled"]).Should().Be(1);
        Convert.ToInt32(rec["is_claimed"]).Should().Be(0, "退款仍需用户主动领取");
        rec["result_note"].ToString().Should().Be("比赛取消并退还");

        // 比赛状态变为 3（已取消）且禁止预测
        using MySQLHelper probe = _fixture.CreateHelper();
        probe.Parameters["@mid"] = mid;
        DataRow? m = probe.ExecuteDataRow("SELECT status, betting_enabled FROM betting_matches WHERE id=@mid");
        m.Should().NotBeNull();
        Convert.ToInt32(m!["status"]).Should().Be(3);
        Convert.ToInt32(m["betting_enabled"]).Should().Be(0);
    }

    // =========================================================================
    // B08 — 领奖一次（净额精确）
    // =========================================================================

    [Fact]
    public async Task ClaimRewards_仅发放一次_第二次返回零()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();
        BettingService service = new(sql, new CandyBotFunctionConfig());
        long uid = SeedUser("u-claim", 50000);
        long mid = SeedMatch(service, team1Odds: 2.0m);
        service.PlaceBet(uid, mid, "team1", 1000, out _).Should().BeTrue();

        using (MySQLHelper s1 = _fixture.CreateHelper())
            new BettingService(s1, new CandyBotFunctionConfig()).SettleMatch((int)mid, "team1", "2:0");

        // payout = 1000 * 2.0 = 2000
        using MySQLHelper c1 = _fixture.CreateHelper();
        long first = new BettingService(c1, new CandyBotFunctionConfig()).ClaimRewards(uid);
        first.Should().Be(2000);

        using MySQLHelper c2 = _fixture.CreateHelper();
        long second = new BettingService(c2, new CandyBotFunctionConfig()).ClaimRewards(uid);
        second.Should().Be(0, "第二次领奖不应重复发放");
    }

    /// <summary>
    /// 端到端净额：模拟处理器的真实资金流（下注扣减 + 结算后领奖发放），断言精确余额。
    /// 处理器逻辑：下注成功后 Credits -= amt；预测领奖后 Credits += ClaimRewards 返回值。
    /// </summary>
    [Fact]
    public async Task 下注扣减加领奖发放_净额精确()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();
        BettingService service = new(sql, new CandyBotFunctionConfig());
        const double initial = 50000;
        long uid = SeedUser("u-net", initial);
        long mid = SeedMatch(service, team1Odds: 2.0m);

        const long bet = 1000;
        service.PlaceBet(uid, mid, "team1", bet, out string be).Should().BeTrue(be);

        // 处理器：下注成功后扣减本金
        using (MySQLHelper debitSql = _fixture.CreateHelper())
        {
            User u = debitSql.GetUserById(uid, loadInventory: true)!;
            u.Inventory.Credits -= bet;
            debitSql.UpdateUser(u);
        }
        GetCredits(uid).Should().Be(initial - bet, "下注后应扣减本金");

        // 结算 team1 胜：payout = 2000
        using (MySQLHelper s = _fixture.CreateHelper())
            new BettingService(s, new CandyBotFunctionConfig()).SettleMatch((int)mid, "team1", "2:0");

        // 处理器：领奖并加到余额
        long payout;
        using (MySQLHelper c = _fixture.CreateHelper())
            payout = new BettingService(c, new CandyBotFunctionConfig()).ClaimRewards(uid);
        payout.Should().Be(2000);
        using (MySQLHelper creditSql = _fixture.CreateHelper())
        {
            User u = creditSql.GetUserById(uid, loadInventory: true)!;
            u.Inventory.Credits += payout;
            creditSql.UpdateUser(u);
        }

        // 净额：50000 - 1000 + 2000 = 51000
        GetCredits(uid).Should().Be(initial - bet + payout);
    }

    // =========================================================================
    // CalculateOdds（纯逻辑，验证 service 公式在真实路径下一致）
    // =========================================================================

    [Fact]
    public void CalculateOdds_按胜率与水位计算()
    {
        using MySQLHelper sql = _fixture.CreateHelper();
        BettingService service = new(sql, new CandyBotFunctionConfig());
        var (oddsA, oddsB) = service.CalculateOdds(0.5m, 0.08m);
        // 1 / (0.5 * 1.08) = 1.851... → 1.85（两位四舍五入）
        oddsA.Should().Be(1.85m);
        oddsB.Should().Be(1.85m);
    }
}
