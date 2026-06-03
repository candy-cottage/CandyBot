using System.Data;
using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Utilities;
using Milimoe.FunGame.Core.Api.Transmittal;

namespace CandyCottage.CandyBot.Services.Esports;

/// <summary>
/// 把外部赛程数据源的比赛幂等地写入投注表，统一落为<b>未发布草稿</b>
/// （<c>is_published=0</c> 同时作用于赛事与比赛），等待管理员发布后才对用户可见。
///
/// 幂等性以外部锚点 <c>external_id</c> 为键：
/// <list type="bullet">
///   <item>赛事 / 比赛已存在则 UPDATE（仅当仍为草稿时刷新阵容/时间），不重复插入；</item>
///   <item>已发布的比赛只在新开赛时间仍在未来时刷新开赛/截止时间（且状态仍未开始），不回退已发布状态。</item>
/// </list>
/// 单场比赛同步失败只记一次告警并计入 <see cref="EsportsScheduleSyncReport.Failed"/>，
/// 不会中断整批同步（批量韧性）。
/// </summary>
public sealed class EsportsSyncService(
    ILogger<EsportsSyncService> logger,
    SQLHelper sql,
    BettingService bettingService,
    IEsportsDataProvider provider,
    PandaScoreConfig config)
{
    public async Task<EsportsScheduleSyncReport> SyncSchedulesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ExternalEsportsMatch> matches = await provider.GetUpcomingMatchesAsync(config.GameSlug, DateTime.UtcNow, cancellationToken);
        int eventsCreated = 0, eventsUpdated = 0, matchesCreated = 0, matchesUpdated = 0, failed = 0;
        List<string> errors = [];

        foreach (ExternalEsportsMatch match in matches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                long eventId = UpsertEvent(match, ref eventsCreated, ref eventsUpdated);
                UpsertMatch(eventId, match, ref matchesCreated, ref matchesUpdated);
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"{match.ExternalId}: {ex.Message}");
                logger.LogWarning(ex, "同步外部比赛失败: {ExternalId}", match.ExternalId);
            }
        }

        return new(matches.Count, eventsCreated, eventsUpdated, matchesCreated, matchesUpdated, failed, errors);
    }

    /// <summary>
    /// 半自动结算的第一步：把外部数据源已终结的<b>已发布胜负比赛</b>结果（result/winner）预填进表，
    /// 并置 <c>pending_settlement=1</c> 等待管理员二次确认结算——<b>不</b>动金钱、<b>不</b>把 <c>status</c> 改为 2（已结束）。
    ///
    /// 候选过滤保证幂等与安全：仅取 <c>external_id</c> 非空、已发布、未结束且未取消（<c>status IN (0,1)</c>）、
    /// 尚未待确认（<c>pending_settlement=0</c>）的比赛——重复运行不会二次预填。UPDATE 的
    /// <c>WHERE ... AND status IN (0,1)</c> 是并发护栏：若另一路径在期间已结算或取消，则本次写入命中 0 行而不覆盖。
    ///
    /// 含比分（score）/MVP 选项的比赛无法仅凭 winner 自动判定，归入 <see cref="EsportsResultSyncReport.RequiresManualSettlement"/>，
    /// 留给管理员手动结算。单场预填失败只记一次告警并计入 <see cref="EsportsResultSyncReport.Failed"/>，不中断整批（批量韧性）。
    /// </summary>
    public async Task<EsportsResultSyncReport> SyncResultsAsync(CancellationToken cancellationToken = default)
    {
        List<(long MatchId, string ExternalId, string Options)> candidates = [];
        sql.ExecuteDataSet(@"SELECT id, external_id, available_options
            FROM betting_matches
            WHERE external_id IS NOT NULL
              AND is_published = 1
              AND status IN (0, 1)
              AND pending_settlement = 0");

        if (sql.Success)
        {
            foreach (DataRow row in sql.DataSet.Tables[0].Rows)
            {
                candidates.Add((Convert.ToInt64(row["id"]), row["external_id"].ToString() ?? "", row["available_options"].ToString() ?? "[]"));
            }
        }

        IReadOnlyList<ExternalEsportsResult> results = await provider.GetFinishedResultsAsync(candidates.Select(c => c.ExternalId).ToArray(), cancellationToken);
        Dictionary<string, ExternalEsportsResult> byExternalId = results.ToDictionary(r => r.ExternalId);
        int pending = 0, manual = 0, failed = 0;
        List<string> errors = [];

        foreach ((long matchId, string externalId, string options) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byExternalId.TryGetValue(externalId, out ExternalEsportsResult? result)) continue;

            if (options.Contains("score", StringComparison.OrdinalIgnoreCase) || options.Contains("mvp", StringComparison.OrdinalIgnoreCase))
            {
                manual++;
                continue;
            }

            try
            {
                sql.Parameters["@mid"] = matchId;
                sql.Parameters["@result"] = result.ResultText;
                sql.Parameters["@winner"] = result.Winner;
                sql.Execute("UPDATE betting_matches SET result=@result, winner=@winner, pending_settlement=1, betting_enabled=0 WHERE id=@mid AND status IN (0, 1)");
                if (!sql.Success) throw sql.LastException ?? new InvalidOperationException("结果预填失败。");
                pending++;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"{externalId}: {ex.Message}");
                logger.LogWarning(ex, "预填比赛结果失败: {ExternalId}", externalId);
            }
        }

        return new(candidates.Count, pending, manual, failed, errors);
    }

    private long UpsertEvent(ExternalEsportsMatch match, ref int created, ref int updated)
    {
        sql.Parameters["@external_id"] = match.EventExternalId;
        DataRow? existing = sql.ExecuteDataRow("SELECT id FROM betting_events WHERE external_id=@external_id");
        if (existing is not null)
        {
            sql.Parameters["@name"] = match.EventName;
            sql.Parameters["@start"] = match.EventStartTime;
            sql.Parameters["@end"] = match.EventEndTime;
            sql.Parameters["@external_id"] = match.EventExternalId;
            sql.Execute("UPDATE betting_events SET name=@name, start_time=@start, end_time=@end WHERE external_id=@external_id AND is_published=0");
            // updated 计的是“命中了已存在的 external_id”（幂等命中），并非“实际改动了行”——
            // 已发布赛事会因 AND is_published=0 而匹配 0 行，但仍计入此处。
            updated++;
            return Convert.ToInt64(existing["id"]);
        }

        sql.Parameters["@external_id"] = match.EventExternalId;
        sql.Parameters["@name"] = match.EventName;
        sql.Parameters["@start"] = match.EventStartTime;
        sql.Parameters["@end"] = match.EventEndTime;
        sql.Execute("INSERT INTO betting_events (external_id, is_published, name, status, start_time, end_time) VALUES (@external_id, 0, @name, 0, @start, @end)");
        if (!sql.Success) throw sql.LastException ?? new InvalidOperationException("赛事草稿写入失败。");
        created++;
        return sql.LastInsertId;
    }

    private void UpsertMatch(long eventId, ExternalEsportsMatch match, ref int created, ref int updated)
    {
        (decimal t1Odds, decimal t2Odds) = ResolveOdds(match);
        DateTime beijingStart = ChinaStandardTime.FromUtc(match.StartTime);

        sql.Parameters["@external_id"] = match.ExternalId;
        DataRow? existing = sql.ExecuteDataRow("SELECT id, is_published, status FROM betting_matches WHERE external_id=@external_id");
        if (existing is not null)
        {
            int isPublished = Convert.ToInt32(existing["is_published"]);
            if (isPublished == 0)
            {
                sql.Parameters["@mid"] = existing["id"];
                sql.Parameters["@eid"] = eventId;
                sql.Parameters["@t1"] = match.Team1Name;
                sql.Parameters["@t2"] = match.Team2Name;
                sql.Parameters["@stage"] = match.Stage;
                sql.Parameters["@start"] = beijingStart;
                sql.Parameters["@deadline"] = beijingStart;
                sql.Parameters["@t1_odds"] = t1Odds;
                sql.Parameters["@t2_odds"] = t2Odds;
                sql.Execute(@"UPDATE betting_matches SET event_id=@eid, team1_name=@t1, team2_name=@t2, stage=@stage,
                    start_time=@start, bet_deadline=@deadline, team1_win_odds=@t1_odds, team2_win_odds=@t2_odds
                    WHERE id=@mid");
            }
            else if (beijingStart > ChinaStandardTime.Now)
            {
                sql.Parameters["@mid"] = existing["id"];
                sql.Parameters["@start"] = beijingStart;
                sql.Parameters["@deadline"] = beijingStart;
                sql.Execute("UPDATE betting_matches SET start_time=@start, bet_deadline=@deadline WHERE id=@mid AND status=0");
            }
            // updated 计的是“命中了已存在的 external_id”（幂等命中），并非“实际改动了行”——
            // 已发布且 status!=0 的比赛会匹配 0 行，但仍计入此处。
            updated++;
            return;
        }

        string options = System.Text.Json.JsonSerializer.Serialize(new[] { "team1_win", "team2_win" });
        sql.Parameters["@external_id"] = match.ExternalId;
        sql.Parameters["@eid"] = eventId;
        sql.Parameters["@t1"] = match.Team1Name;
        sql.Parameters["@t2"] = match.Team2Name;
        sql.Parameters["@stage"] = match.Stage;
        sql.Parameters["@start"] = beijingStart;
        sql.Parameters["@deadline"] = beijingStart;
        sql.Parameters["@opts"] = options;
        sql.Parameters["@t1_odds"] = t1Odds;
        sql.Parameters["@t2_odds"] = t2Odds;
        sql.Execute(@"INSERT INTO betting_matches
            (external_id, is_published, pending_settlement, event_id, team1_name, team2_name, stage, start_time, bet_deadline,
             available_options, team1_win_odds, team2_win_odds, status, betting_enabled)
            VALUES (@external_id, 0, 0, @eid, @t1, @t2, @stage, @start, @deadline, @opts, @t1_odds, @t2_odds, 0, 1)");
        if (!sql.Success) throw sql.LastException ?? new InvalidOperationException("比赛草稿写入失败。");
        created++;
    }

    private (decimal Team1, decimal Team2) ResolveOdds(ExternalEsportsMatch match)
    {
        if (match.Team1WinOdds.HasValue && match.Team2WinOdds.HasValue)
        {
            return (match.Team1WinOdds.Value, match.Team2WinOdds.Value);
        }
        if (match.Team1WinProbability.HasValue)
        {
            return bettingService.CalculateOdds(match.Team1WinProbability.Value);
        }
        // TODO(debt): wire PandaScore Odds product fields once the account has confirmed odds API access.
        return bettingService.CalculateOdds(0.5m);
    }
}
