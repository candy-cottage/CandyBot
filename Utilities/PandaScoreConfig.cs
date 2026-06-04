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
    /// 优先级：环境变量 &gt; PluginConfig &gt; 默认值。环境变量：
    /// CANDYBOT_PANDASCORE_APIKEY / CANDYBOT_PANDASCORE_BASEURL / CANDYBOT_PANDASCORE_GAMESLUG /
    /// CANDYBOT_PANDASCORE_LEAGUEIDSORSLUGS / CANDYBOT_PANDASCORE_SERIEIDSORSLUGS /
    /// CANDYBOT_PANDASCORE_TOURNAMENTIDSORSLUGS /
    /// CANDYBOT_PANDASCORE_SCHEDULEPOLLINTERVALMINUTES(int) / CANDYBOT_PANDASCORE_RESULTPOLLINTERVALMINUTES(int)。
    /// LeagueIdsOrSlugs / SerieIdsOrSlugs / TournamentIdsOrSlugs 为逗号分隔列表；
    /// 任一项非空时，赛程同步只拉取对应赛事范围的比赛。
    /// 环境变量覆盖仅作用于内存，不写回配置文件。轮询间隔下限为 1 分钟。
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
        scheduleMinutes = ConfigEnv.Override("CANDYBOT_PANDASCORE_SCHEDULEPOLLINTERVALMINUTES", scheduleMinutes);
        resultMinutes = ConfigEnv.Override("CANDYBOT_PANDASCORE_RESULTPOLLINTERVALMINUTES", resultMinutes);

        SchedulePollInterval = TimeSpan.FromMinutes(Math.Max(1, scheduleMinutes));
        ResultPollInterval = TimeSpan.FromMinutes(Math.Max(1, resultMinutes));
    }
}
