using Milimoe.FunGame.Core.Api.Utility;

namespace CandyCottage.CandyBot.Utilities;

public class PandaScoreConfig
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.pandascore.co";
    public string GameSlug { get; set; } = "csgo";
    public string LeagueIdsOrSlugs { get; set; } = "";
    public string SerieIdsOrSlugs { get; set; } = "";
    public string TournamentIdsOrSlugs { get; set; } = "";
    public TimeSpan SchedulePollInterval { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan ResultPollInterval { get; set; } = TimeSpan.FromMinutes(5);

    public PluginConfig Config { get; set; } = new("candybot", "pandascore");

    /// <summary>
    /// 作用域筛选是否由环境变量锁定（任一 *IDSORSLUGS 环境变量生效即锁定）。
    /// 锁定期间禁止在线修改筛选：此时属性持有 env 注入值，
    /// 任何 <see cref="PersistScopeFilters"/> 都会把这些值写盘，违反"环境变量覆盖仅内存生效"的契约。
    /// </summary>
    public bool ScopeFiltersLockedByEnv { get; private set; }

    /// <summary>
    /// 优先级：环境变量 &gt; PluginConfig &gt; 默认值。环境变量：
    /// CANDYBOT_PANDASCORE_APIKEY / CANDYBOT_PANDASCORE_BASEURL / CANDYBOT_PANDASCORE_GAMESLUG /
    /// CANDYBOT_PANDASCORE_LEAGUEIDSORSLUGS / CANDYBOT_PANDASCORE_SERIEIDSORSLUGS /
    /// CANDYBOT_PANDASCORE_TOURNAMENTIDSORSLUGS /
    /// CANDYBOT_PANDASCORE_SCHEDULEPOLLINTERVALMINUTES(int) / CANDYBOT_PANDASCORE_RESULTPOLLINTERVALMINUTES(int)。
    /// LeagueIdsOrSlugs / SerieIdsOrSlugs / TournamentIdsOrSlugs 为逗号分隔列表；
    /// 任一项非空时，赛程同步只拉取对应赛事范围的比赛。
    /// 环境变量覆盖仅作用于内存，不写回配置文件。轮询间隔下限为 1 分钟。
    /// 任一 *IDSORSLUGS 环境变量生效时 <see cref="ScopeFiltersLockedByEnv"/> 为 true，在线修改筛选被整体拒绝。
    /// </summary>
    public void Init()
    {
        Config.LoadConfig();
        ApiKey = Config.GetOrAdd("ApiKey", ApiKey) ?? "";
        BaseUrl = Config.GetOrAdd("BaseUrl", BaseUrl) ?? "";
        GameSlug = Config.GetOrAdd("GameSlug", GameSlug) ?? "";
        LeagueIdsOrSlugs = Config.GetOrAdd("LeagueIdsOrSlugs", LeagueIdsOrSlugs) ?? "";
        SerieIdsOrSlugs = Config.GetOrAdd("SerieIdsOrSlugs", SerieIdsOrSlugs) ?? "";
        TournamentIdsOrSlugs = Config.GetOrAdd("TournamentIdsOrSlugs", TournamentIdsOrSlugs) ?? "";
        int scheduleMinutes = Config.GetOrAdd("SchedulePollIntervalMinutes", (int)SchedulePollInterval.TotalMinutes);
        int resultMinutes = Config.GetOrAdd("ResultPollIntervalMinutes", (int)ResultPollInterval.TotalMinutes);
        Config.SaveConfig();

        // 环境变量覆盖（在 SaveConfig 之后，仅内存生效，不持久化注入的机密）。
        ApiKey = ConfigEnv.Override("CANDYBOT_PANDASCORE_APIKEY", ApiKey);
        BaseUrl = ConfigEnv.Override("CANDYBOT_PANDASCORE_BASEURL", BaseUrl);
        GameSlug = ConfigEnv.Override("CANDYBOT_PANDASCORE_GAMESLUG", GameSlug);
        LeagueIdsOrSlugs = ConfigEnv.Override("CANDYBOT_PANDASCORE_LEAGUEIDSORSLUGS", LeagueIdsOrSlugs);
        SerieIdsOrSlugs = ConfigEnv.Override("CANDYBOT_PANDASCORE_SERIEIDSORSLUGS", SerieIdsOrSlugs);
        TournamentIdsOrSlugs = ConfigEnv.Override("CANDYBOT_PANDASCORE_TOURNAMENTIDSORSLUGS", TournamentIdsOrSlugs);
        // PersistScopeFilters 写盘时三个字段一起写，故任一 env 生效就整体锁定在线修改。
        ScopeFiltersLockedByEnv = ConfigEnv.IsSet("CANDYBOT_PANDASCORE_LEAGUEIDSORSLUGS")
            || ConfigEnv.IsSet("CANDYBOT_PANDASCORE_SERIEIDSORSLUGS")
            || ConfigEnv.IsSet("CANDYBOT_PANDASCORE_TOURNAMENTIDSORSLUGS");
        scheduleMinutes = ConfigEnv.Override("CANDYBOT_PANDASCORE_SCHEDULEPOLLINTERVALMINUTES", scheduleMinutes);
        resultMinutes = ConfigEnv.Override("CANDYBOT_PANDASCORE_RESULTPOLLINTERVALMINUTES", resultMinutes);

        SchedulePollInterval = TimeSpan.FromMinutes(Math.Max(1, scheduleMinutes));
        ResultPollInterval = TimeSpan.FromMinutes(Math.Max(1, resultMinutes));
    }

    public void PersistScopeFilters()
    {
        // 兜底护栏：锁定期间属性持有 env 注入值，绝不能写回配置文件（调用方应先行拒绝）。
        if (ScopeFiltersLockedByEnv) return;
        Config["LeagueIdsOrSlugs"] = LeagueIdsOrSlugs;
        Config["SerieIdsOrSlugs"] = SerieIdsOrSlugs;
        Config["TournamentIdsOrSlugs"] = TournamentIdsOrSlugs;
        Config.SaveConfig();
    }
}
