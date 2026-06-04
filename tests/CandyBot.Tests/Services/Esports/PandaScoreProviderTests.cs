using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Services.Esports;
using CandyCottage.CandyBot.Utilities;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CandyBot.Tests.Services.Esports;

public class PandaScoreProviderTests
{
    [Fact]
    public async Task GetUpcomingMatchesAsync_未配置赛事筛选时_保持按游戏拉取全量赛程()
    {
        RecordingHttpMessageHandler handler = new(SampleMatchListJson);
        using HttpClient client = new(handler);
        using MemoryCache cache = new(new MemoryCacheOptions());
        PandaScoreProvider provider = new(
            client,
            cache,
            new PandaScoreConfig { ApiKey = "token", BaseUrl = "https://example.test", GameSlug = "csgo" },
            NullLogger<PandaScoreProvider>.Instance);

        await provider.GetUpcomingMatchesAsync("csgo", DateTime.SpecifyKind(new DateTime(2026, 6, 1, 0, 0, 0), DateTimeKind.Utc));

        handler.RequestUris.Should().ContainSingle();
        handler.RequestUris[0].AbsolutePath.Should().Be("/csgo/matches");
        handler.RequestUris[0].Query.Should().Contain("filter[status]=not_started");
    }

    [Fact]
    public async Task GetUpcomingMatchesAsync_配置赛事筛选时_只请求对应赛事端点且按比赛去重()
    {
        RecordingHttpMessageHandler handler = new(SampleMatchListJson);
        using HttpClient client = new(handler);
        using MemoryCache cache = new(new MemoryCacheOptions());
        PandaScoreProvider provider = new(
            client,
            cache,
            new PandaScoreConfig
            {
                ApiKey = "token",
                BaseUrl = "https://example.test",
                GameSlug = "csgo",
                LeagueIdsOrSlugs = "league-a, league-b",
                SerieIdsOrSlugs = "serie-1",
                TournamentIdsOrSlugs = "tournament-x",
            },
            NullLogger<PandaScoreProvider>.Instance);

        IReadOnlyList<ExternalEsportsMatch> matches = await provider.GetUpcomingMatchesAsync(
            "csgo",
            DateTime.SpecifyKind(new DateTime(2026, 6, 1, 0, 0, 0), DateTimeKind.Utc));

        matches.Should().ContainSingle("同一场比赛可能同时出现在 league/serie/tournament 端点，入库前必须按 external_id 去重");
        handler.RequestUris.Select(uri => uri.AbsolutePath).Should().BeEquivalentTo(
            "/leagues/league-a/matches",
            "/leagues/league-b/matches",
            "/series/serie-1/matches",
            "/tournaments/tournament-x/matches");
        handler.RequestUris.Should().OnlyContain(uri => !uri.AbsolutePath.StartsWith("/csgo/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetUpcomingMatchesAsync_同一小时内重复调用_命中缓存不重复请求()
    {
        RecordingHttpMessageHandler handler = new(SampleMatchListJson);
        using HttpClient client = new(handler);
        using MemoryCache cache = new(new MemoryCacheOptions());
        PandaScoreProvider provider = new(
            client,
            cache,
            new PandaScoreConfig
            {
                ApiKey = "token",
                BaseUrl = "https://example.test",
                GameSlug = "csgo",
                LeagueIdsOrSlugs = "league-a",
            },
            NullLogger<PandaScoreProvider>.Instance);

        // 调度器与"预览赛事筛选"都传入 DateTime.UtcNow——同一小时内的两次调用必须复用缓存，
        // 否则每次同步都会对每个作用域重复请求 PandaScore。
        await provider.GetUpcomingMatchesAsync("csgo", DateTime.SpecifyKind(new DateTime(2026, 6, 1, 10, 5, 17), DateTimeKind.Utc));
        await provider.GetUpcomingMatchesAsync("csgo", DateTime.SpecifyKind(new DateTime(2026, 6, 1, 10, 25, 42), DateTimeKind.Utc));

        handler.RequestUris.Should().ContainSingle("同一小时内的第二次调用应命中缓存，不再发起 HTTP 请求");
    }

    [Fact]
    public async Task GetUpcomingMatchesAsync_单个作用域请求失败_不影响其他作用域()
    {
        // 失效/误填的筛选（PandaScore 404）只应跳过该作用域并记录告警，
        // 不得中断整轮同步——否则一个坏筛选会阻塞全部比赛导入。
        RecordingHttpMessageHandler handler = new(SampleMatchListJson)
        {
            PathStatusOverrides = { ["/leagues/bad-league/matches"] = System.Net.HttpStatusCode.NotFound },
        };
        using HttpClient client = new(handler);
        using MemoryCache cache = new(new MemoryCacheOptions());
        PandaScoreProvider provider = new(
            client,
            cache,
            new PandaScoreConfig
            {
                ApiKey = "token",
                BaseUrl = "https://example.test",
                GameSlug = "csgo",
                LeagueIdsOrSlugs = "bad-league,good-league",
            },
            NullLogger<PandaScoreProvider>.Instance);

        IReadOnlyList<ExternalEsportsMatch> matches = await provider.GetUpcomingMatchesAsync(
            "csgo",
            DateTime.SpecifyKind(new DateTime(2026, 6, 1, 0, 0, 0), DateTimeKind.Utc));

        matches.Should().ContainSingle("好作用域的比赛应正常返回，坏作用域只被跳过");
        handler.RequestUris.Should().HaveCount(2, "坏作用域失败后仍应继续请求剩余作用域");
    }

    [Fact]
    public async Task GetUpcomingMatchesAsync_全部作用域请求失败_抛出异常()
    {
        // 全部失败说明是整体故障（如 API 不可用），必须抛出让调度器按原节律重试，
        // 而不是缓存一份空结果掩盖故障。
        RecordingHttpMessageHandler handler = new(SampleMatchListJson)
        {
            PathStatusOverrides = { ["/leagues/bad-league/matches"] = System.Net.HttpStatusCode.NotFound },
        };
        using HttpClient client = new(handler);
        using MemoryCache cache = new(new MemoryCacheOptions());
        PandaScoreProvider provider = new(
            client,
            cache,
            new PandaScoreConfig
            {
                ApiKey = "token",
                BaseUrl = "https://example.test",
                GameSlug = "csgo",
                LeagueIdsOrSlugs = "bad-league",
            },
            NullLogger<PandaScoreProvider>.Instance);

        Func<Task> act = () => provider.GetUpcomingMatchesAsync(
            "csgo",
            DateTime.SpecifyKind(new DateTime(2026, 6, 1, 0, 0, 0), DateTimeKind.Utc));

        await act.Should().ThrowAsync<Exception>("全部作用域失败不应被静默吞掉");
    }

    [Fact]
    public async Task SearchScopesAsync_按游戏搜索三层赛事候选项()
    {
        RecordingHttpMessageHandler handler = new(SampleScopeListJson);
        using HttpClient client = new(handler);
        using MemoryCache cache = new(new MemoryCacheOptions());
        PandaScoreProvider provider = new(
            client,
            cache,
            new PandaScoreConfig { ApiKey = "token", BaseUrl = "https://example.test", GameSlug = "csgo" },
            NullLogger<PandaScoreProvider>.Instance);

        IReadOnlyList<ExternalEsportsScope> scopes = await provider.SearchScopesAsync("csgo", "blast");

        // 锦标赛搜索必须走按游戏限定的端点（/csgo/tournaments），否则全局 /tournaments
        // 会返回其他游戏的锦标赛，管理员误加后会把非 CS 比赛同步进投注草稿。
        handler.RequestUris.Select(uri => uri.AbsolutePath).Should().BeEquivalentTo(
            "/videogames/csgo/leagues",
            "/videogames/csgo/series",
            "/csgo/tournaments");
        handler.RequestUris.Should().OnlyContain(uri => uri.Query.Contains("search[name]=blast", StringComparison.Ordinal));
        scopes.Should().HaveCount(3);
        scopes.Should().Contain(scope => scope.Type == "league" && scope.Value == "blast-premier");
        scopes.Should().Contain(scope => scope.Type == "serie" && scope.ParentName == "BLAST Premier");
        scopes.Should().Contain(scope => scope.Type == "tournament" && scope.ParentName == "BLAST Premier 2026");
    }

    [Fact]
    public void MapMatch_两队未开赛比赛_映射为外部比赛草稿()
    {
        const string json = """
        {
          "id": 647619,
          "name": "NAVI vs FaZe",
          "status": "not_started",
          "scheduled_at": "2026-06-10T12:00:00Z",
          "begin_at": "2026-06-10T12:00:00Z",
          "end_at": null,
          "league": { "id": 11, "name": "ESL Pro League" },
          "serie": { "id": 22, "full_name": "Season 23" },
          "tournament": { "id": 33, "name": "Group Stage" },
          "opponents": [
            { "opponent": { "id": 100, "name": "NAVI" }, "type": "Team" },
            { "opponent": { "id": 200, "name": "FaZe" }, "type": "Team" }
          ],
          "winner_id": null,
          "results": []
        }
        """;

        ExternalEsportsMatch match = PandaScoreMapper.MapMatch(json);

        match.ExternalId.Should().Be("647619");
        match.EventExternalId.Should().Be("11:22:33");
        match.EventName.Should().Be("ESL Pro League Season 23");
        match.Team1Name.Should().Be("NAVI");
        match.Team2Name.Should().Be("FaZe");
        match.StartTime.Should().Be(DateTime.Parse("2026-06-10T12:00:00Z").ToUniversalTime());
        match.Stage.Should().Be("Group Stage");
        match.Team1WinProbability.Should().BeNull("fixtures/statistics API does not provide bookmaker odds by default");
    }

    [Fact]
    public void MapMatch_跨UTC日期比赛_赛事窗口默认使用北京时间日期()
    {
        const string json = """
        {
          "id": 647620,
          "name": "NAVI vs FaZe",
          "status": "not_started",
          "scheduled_at": "2026-06-10T18:00:00Z",
          "league": { "id": 11, "name": "ESL Pro League" },
          "serie": { "id": 22, "full_name": "Season 23" },
          "tournament": { "id": 33, "name": "Group Stage" },
          "opponents": [
            { "opponent": { "id": 100, "name": "NAVI" }, "type": "Team" },
            { "opponent": { "id": 200, "name": "FaZe" }, "type": "Team" }
          ]
        }
        """;

        ExternalEsportsMatch match = PandaScoreMapper.MapMatch(json);

        match.StartTime.Should().Be(DateTime.SpecifyKind(new DateTime(2026, 6, 10, 18, 0, 0), DateTimeKind.Utc));
        match.EventStartTime.Should().Be(new DateTime(2026, 6, 11, 0, 0, 0));
        match.EventEndTime.Should().Be(new DateTime(2026, 6, 12, 0, 0, 0));
    }

    [Fact]
    public void MapResult_已结束比赛_映射winner与比分()
    {
        const string json = """
        {
          "id": 647619,
          "status": "finished",
          "winner_id": 100,
          "opponents": [
            { "opponent": { "id": 100, "name": "NAVI" }, "type": "Team" },
            { "opponent": { "id": 200, "name": "FaZe" }, "type": "Team" }
          ],
          "results": [
            { "team_id": 100, "score": 2 },
            { "team_id": 200, "score": 1 }
          ]
        }
        """;

        ExternalEsportsResult result = PandaScoreMapper.MapResult(json);

        result.ExternalId.Should().Be("647619");
        result.Winner.Should().Be(1);
        result.ResultText.Should().Be("2:1");
    }

    [Fact]
    public void MapMatches_缺少opponents字段的比赛_不抛异常且被过滤()
    {
        // PandaScore 对 TBD / 刚公布的赛程常常完全省略 opponents/league/serie/tournament 等键，
        // System.Text.Json 会将其留为 null。整批映射不得因单条畸形数据而抛异常。
        const string json = """
        [
          {
            "id": 1,
            "name": "TBD vs TBD",
            "status": "not_started",
            "scheduled_at": "2026-06-10T12:00:00Z"
          },
          {
            "id": 2,
            "name": "NAVI vs FaZe",
            "status": "not_started",
            "scheduled_at": "2026-06-11T12:00:00Z",
            "league": { "id": 11, "name": "ESL Pro League" },
            "serie": { "id": 22, "full_name": "Season 23" },
            "tournament": { "id": 33, "name": "Group Stage" },
            "opponents": [
              { "opponent": { "id": 100, "name": "NAVI" }, "type": "Team" },
              { "opponent": { "id": 200, "name": "FaZe" }, "type": "Team" }
            ]
          }
        ]
        """;

        List<ExternalEsportsMatch> matches = PandaScoreMapper.MapMatches(json);

        // 缺少 opponents 的那条因 Team1Name/Team2Name 为空被过滤，仅保留有效的一条。
        matches.Should().ContainSingle();
        matches[0].ExternalId.Should().Be("2");
        matches[0].Team1Name.Should().Be("NAVI");
        matches[0].Team2Name.Should().Be("FaZe");
    }

    [Fact]
    public void MapResults_缺少results字段_不抛异常且比分为零()
    {
        // 已结束但 results/opponents 缺失的畸形数据不得令整批 MapResults 抛异常。
        const string json = """
        [
          {
            "id": 1,
            "status": "finished",
            "winner_id": 999
          },
          {
            "id": 2,
            "status": "finished",
            "winner_id": 100,
            "opponents": [
              { "opponent": { "id": 100, "name": "NAVI" }, "type": "Team" },
              { "opponent": { "id": 200, "name": "FaZe" }, "type": "Team" }
            ],
            "results": [
              { "team_id": 100, "score": 2 },
              { "team_id": 200, "score": 1 }
            ]
          }
        ]
        """;

        List<ExternalEsportsResult> results = PandaScoreMapper.MapResults(json);

        results.Should().HaveCount(2);
        // 缺失 results/opponents 的元素：Winner 0、比分 0:0，但不抛异常。
        results[0].ExternalId.Should().Be("1");
        results[0].Winner.Should().Be(0);
        results[0].ResultText.Should().Be("0:0");
        // 正常元素照常映射。
        results[1].ExternalId.Should().Be("2");
        results[1].Winner.Should().Be(1);
        results[1].ResultText.Should().Be("2:1");
    }

    private const string SampleMatchListJson = """
    [
      {
        "id": 647619,
        "name": "NAVI vs FaZe",
        "status": "not_started",
        "scheduled_at": "2026-06-10T12:00:00Z",
        "league": { "id": 11, "name": "ESL Pro League" },
        "serie": { "id": 22, "full_name": "Season 23" },
        "tournament": { "id": 33, "name": "Group Stage" },
        "opponents": [
          { "opponent": { "id": 100, "name": "NAVI" }, "type": "Team" },
          { "opponent": { "id": 200, "name": "FaZe" }, "type": "Team" }
        ]
      }
    ]
    """;

    private const string SampleScopeListJson = """
    [
      {
        "id": 4197,
        "name": "BLAST Premier",
        "full_name": "BLAST Premier 2026",
        "slug": "blast-premier",
        "league": { "id": 4197, "name": "BLAST Premier" },
        "serie": { "id": 6201, "name": "2026", "full_name": "BLAST Premier 2026" }
      }
    ]
    """;

    private sealed class RecordingHttpMessageHandler(string responseBody) : HttpMessageHandler
    {
        public List<Uri> RequestUris { get; } = [];

        /// <summary>按请求路径覆盖响应状态码（如模拟某个作用域 404），未命中的路径返回 200 + responseBody。</summary>
        public Dictionary<string, System.Net.HttpStatusCode> PathStatusOverrides { get; init; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri ?? throw new InvalidOperationException("请求 URI 不能为空。");
            RequestUris.Add(uri);
            System.Net.HttpStatusCode status = PathStatusOverrides.GetValueOrDefault(uri.AbsolutePath, System.Net.HttpStatusCode.OK);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(status == System.Net.HttpStatusCode.OK ? responseBody : ""),
            });
        }
    }
}
