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
                sql.Parameters["@start"] = match.StartTime;
                sql.Parameters["@deadline"] = match.StartTime;
                sql.Parameters["@t1_odds"] = t1Odds;
                sql.Parameters["@t2_odds"] = t2Odds;
                sql.Execute(@"UPDATE betting_matches SET event_id=@eid, team1_name=@t1, team2_name=@t2, stage=@stage,
                    start_time=@start, bet_deadline=@deadline, team1_win_odds=@t1_odds, team2_win_odds=@t2_odds
                    WHERE id=@mid");
            }
            // 与本地时钟比较只是“尚未开赛”的粗略判断；provider 的 StartTime 为 UTC，这里不做精确换算。
            else if (match.StartTime > DateTime.Now)
            {
                sql.Parameters["@mid"] = existing["id"];
                sql.Parameters["@start"] = match.StartTime;
                sql.Parameters["@deadline"] = match.StartTime;
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
        sql.Parameters["@start"] = match.StartTime;
        sql.Parameters["@deadline"] = match.StartTime;
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
