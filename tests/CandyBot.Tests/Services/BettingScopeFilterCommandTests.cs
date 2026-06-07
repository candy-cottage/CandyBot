using CandyBot.Tests.Fakes;
using CandyBot.Tests.Utilities;
using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Services;
using CandyCottage.CandyBot.Services.BotServices;
using CandyCottage.CandyBot.Services.Esports;
using CandyCottage.CandyBot.Utilities;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Milimoe.FunGame.Core.Api.Utility;
using Milimoe.FunGame.Core.Entity;
using Xunit;

namespace CandyBot.Tests.Services;

[Collection(PluginConfigCollection.Name)]
public class BettingScopeFilterCommandTests
{
    private sealed class FakeProvider : IEsportsDataProvider, IEsportsScopeSearchProvider
    {
        public IReadOnlyList<ExternalEsportsScope> ScopeResults { get; init; } = [];
        public IReadOnlyList<ExternalEsportsMatch> PreviewMatches { get; init; } = [];
        public string? LastSearchKeyword { get; private set; }

        public Task<IReadOnlyList<ExternalEsportsMatch>> GetUpcomingMatchesAsync(string gameSlug, DateTime since, CancellationToken cancellationToken = default)
            => Task.FromResult(PreviewMatches);

        public Task<IReadOnlyList<ExternalEsportsResult>> GetFinishedResultsAsync(IReadOnlyCollection<string> externalIds, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ExternalEsportsResult>>([]);

        public Task<IReadOnlyList<ExternalEsportsScope>> SearchScopesAsync(string gameSlug, string keyword, CancellationToken cancellationToken = default)
        {
            LastSearchKeyword = keyword;
            return Task.FromResult(ScopeResults);
        }
    }

    private static User AdminUser() => TestData.CreateUser(isAdmin: true, openid: AdminOpenId);

    private const string AdminOpenId = "admin-openid";

    private static ExternalEsportsMatch Match(string id, DateTime start) =>
        new(
            ExternalId: id,
            EventExternalId: "league:serie:tournament",
            EventName: "BLAST Premier 2026",
            EventStartTime: start.Date,
            EventEndTime: start.Date.AddDays(1),
            Team1Name: "NAVI",
            Team2Name: "FaZe",
            StartTime: start,
            Stage: "Group Stage");

    private static CandyBotService CreateService(
        IBotReplySender sender,
        IMemoryCache cache,
        PandaScoreConfig pandaScoreConfig,
        IEsportsDataProvider provider)
    {
        CandyBotFunctionConfig functionConfig = new()
        {
            System = true,
            Betting = true,
            BettingAutomation = true,
        };

        EsportsSyncService esportsSyncService = new(
            NullLogger<EsportsSyncService>.Instance,
            sql: null!,
            bettingService: null!,
            provider,
            pandaScoreConfig);

        return new(
            NullLogger<CandyBotService>.Instance,
            cache,
            sql: null!,
            functionConfig,
            forzaConfig: null!,
            taskQueue: null!,
            taskManager: null!,
            sender,
            bettingService: null!,
            sqlInitService: null!,
            userService: null!,
            forzaService: null!,
            forzaSheetImporter: null!,
            esportsSyncService,
            userAPITokenService: null!);
    }

    private static Task WithTempPluginRoot(Func<PandaScoreConfig, Task> body)
        => WithEnvAndTempPluginRoot([], body);

    /// <summary>在临时 RootPath 与指定环境变量下构造并 Init 一个 PandaScoreConfig，结束后恢复全部进程级状态。</summary>
    private static async Task WithEnvAndTempPluginRoot(Dictionary<string, string?> envVars, Func<PandaScoreConfig, Task> body)
    {
        string originalRoot = PluginConfig.RootPath;
        string tmp = Path.Combine(Path.GetTempPath(), "candybot-scope-filter-test-" + Guid.NewGuid().ToString("N"));
        Dictionary<string, string?> originals = [];
        foreach (string name in envVars.Keys)
        {
            originals[name] = Environment.GetEnvironmentVariable(name);
        }
        try
        {
            PluginConfig.RootPath = tmp;
            foreach ((string name, string? value) in envVars)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
            PandaScoreConfig config = new()
            {
                ApiKey = "token",
                BaseUrl = "https://example.test",
                GameSlug = "csgo",
                Config = new PluginConfig("candybot", "pandascore"),
            };
            config.Init();

            await body(config);
        }
        finally
        {
            foreach ((string name, string? value) in originals)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
            PluginConfig.RootPath = originalRoot;
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
        }
    }

    [Fact]
    public async Task 管理员搜索赛事筛选后可用序号添加并持久化()
    {
        await WithTempPluginRoot(async config =>
        {
            RecordingBotReplySender sender = new();
            using MemoryCache cache = new(new MemoryCacheOptions());
            FakeProvider provider = new()
            {
                ScopeResults =
                [
                    new("league", "BLAST Premier", "blast-premier", 4197),
                    new("serie", "BLAST Premier 2026", "blast-premier-2026", 6201, "BLAST Premier"),
                ],
            };
            CandyBotService service = CreateService(sender, cache, config, provider);

            bool searched = await service.HandleBettingInput(TestData.CreateMessage("搜索赛事筛选 blast", AdminOpenId), AdminUser());
            bool added = await service.HandleBettingInput(TestData.CreateMessage("添加赛事筛选 2", AdminOpenId), AdminUser());

            searched.Should().BeTrue();
            added.Should().BeTrue();
            provider.LastSearchKeyword.Should().Be("blast");
            sender.TextContents.First().Should()
                .Contain("1. [league] BLAST Premier")
                .And.Contain("2. [serie] BLAST Premier 2026")
                .And.Contain("添加赛事筛选 2");
            sender.TextContents.Last().Should().Contain("已添加赛事筛选：serie blast-premier-2026");
            config.SerieIdsOrSlugs.Should().Be("blast-premier-2026");

            PluginConfig reloaded = new("candybot", "pandascore");
            reloaded.LoadConfig();
            (reloaded.GetOrAdd("SerieIdsOrSlugs", "") ?? "").Should().Be("blast-premier-2026");
        });
    }

    [Fact]
    public async Task 管理员可查看移除并清空赛事筛选()
    {
        await WithTempPluginRoot(async config =>
        {
            config.LeagueIdsOrSlugs = "blast-premier,esl-pro-league";
            config.SerieIdsOrSlugs = "blast-premier-2026";
            config.PersistScopeFilters();
            RecordingBotReplySender sender = new();
            using MemoryCache cache = new(new MemoryCacheOptions());
            CandyBotService service = CreateService(sender, cache, config, new FakeProvider());

            bool viewed = await service.HandleBettingInput(TestData.CreateMessage("查看赛事筛选", AdminOpenId), AdminUser());
            bool removed = await service.HandleBettingInput(TestData.CreateMessage("移除赛事筛选 league blast-premier", AdminOpenId), AdminUser());
            bool cleared = await service.HandleBettingInput(TestData.CreateMessage("清空赛事筛选", AdminOpenId), AdminUser());

            viewed.Should().BeTrue();
            removed.Should().BeTrue();
            cleared.Should().BeTrue();
            sender.TextContents.ElementAt(0).Should().Contain("league: blast-premier, esl-pro-league");
            sender.TextContents.ElementAt(1).Should().Contain("已移除赛事筛选：league blast-premier");
            sender.TextContents.ElementAt(1).Should().Contain("league: esl-pro-league");
            config.LeagueIdsOrSlugs.Should().Be("");
            config.SerieIdsOrSlugs.Should().Be("");
            sender.TextContents.Last().Should().Contain("已清空赛事筛选。");
        });
    }

    [Fact]
    public async Task 普通用户不能修改赛事筛选()
    {
        await WithTempPluginRoot(async config =>
        {
            RecordingBotReplySender sender = new();
            using MemoryCache cache = new(new MemoryCacheOptions());
            CandyBotService service = CreateService(sender, cache, config, new FakeProvider());

            bool handled = await service.HandleBettingInput(
                TestData.CreateMessage("添加赛事筛选 league blast-premier", "user-openid"),
                TestData.CreateUser(openid: "user-openid"));

            handled.Should().BeTrue();
            sender.TextContents.Should().ContainSingle().Which.Should().Be("你没有权限执行此操作。");
            config.LeagueIdsOrSlugs.Should().Be("");
        });
    }

    [Fact]
    public async Task 环境变量锁定时_拒绝在线修改且不落盘环境值()
    {
        await WithEnvAndTempPluginRoot(
            new Dictionary<string, string?> { ["CANDYBOT_PANDASCORE_LEAGUEIDSORSLUGS"] = "league-env" },
            async config =>
            {
                RecordingBotReplySender sender = new();
                using MemoryCache cache = new(new MemoryCacheOptions());
                CandyBotService service = CreateService(sender, cache, config, new FakeProvider());

                bool added = await service.HandleBettingInput(TestData.CreateMessage("添加赛事筛选 serie blast-2026", AdminOpenId), AdminUser());
                bool removed = await service.HandleBettingInput(TestData.CreateMessage("移除赛事筛选 league league-env", AdminOpenId), AdminUser());
                bool cleared = await service.HandleBettingInput(TestData.CreateMessage("清空赛事筛选", AdminOpenId), AdminUser());

                added.Should().BeTrue();
                removed.Should().BeTrue();
                cleared.Should().BeTrue();
                sender.TextContents.Should().HaveCount(3).And.OnlyContain(text => text.Contains("环境变量锁定"));
                config.LeagueIdsOrSlugs.Should().Be("league-env", "锁定期间清空/移除不应生效");

                // 关键回归：环境变量注入的筛选值绝不能写回配置文件。
                PluginConfig reloaded = new("candybot", "pandascore");
                reloaded.LoadConfig();
                (reloaded.GetOrAdd("LeagueIdsOrSlugs", "") ?? "").Should().Be("");
                (reloaded.GetOrAdd("SerieIdsOrSlugs", "") ?? "").Should().Be("");
            });
    }

    [Fact]
    public async Task 管理员预览赛事筛选_显示候选比赛()
    {
        await WithTempPluginRoot(async config =>
        {
            config.LeagueIdsOrSlugs = "blast-premier";
            RecordingBotReplySender sender = new();
            using MemoryCache cache = new(new MemoryCacheOptions());
            CandyBotService service = CreateService(sender, cache, config, new FakeProvider
            {
                PreviewMatches = [Match("match-1", DateTime.UtcNow.AddDays(3))],
            });

            bool handled = await service.HandleBettingInput(TestData.CreateMessage("预览赛事筛选", AdminOpenId), AdminUser());

            handled.Should().BeTrue();
            sender.TextContents.Should().ContainSingle().Which.Should()
                .Contain("当前筛选可抓取 1 场未开始比赛")
                .And.Contain("NAVI vs FaZe");
        });
    }
}
