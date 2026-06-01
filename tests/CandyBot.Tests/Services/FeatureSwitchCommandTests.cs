using CandyBot.Tests.Fakes;
using CandyBot.Tests.Utilities;
using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Services;
using CandyCottage.CandyBot.Services.BotServices;
using CandyCottage.CandyBot.Utilities;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Milimoe.FunGame.Core.Api.Utility;
using Xunit;

namespace CandyBot.Tests.Services;

/// <summary>
/// B04（plan step 4）：开关指令应写入正确的配置值并发送正确的中文回复。
/// HandleToggleCommand 会调用 Config.SaveConfig()（文件 I/O），故借助进程级
/// 静态 PluginConfig.RootPath 重定向到临时目录，并归入禁用并行的集合。
/// </summary>
[Collection(PluginConfigCollection.Name)]
public class FeatureSwitchCommandTests
{
    private static CandyBotService CreateService(IBotReplySender botService, CandyBotFunctionConfig cfg, IMemoryCache cache) =>
        new(NullLogger<CandyBotService>.Instance, cache, sql: null!, cfg, taskQueue: null!,
            botService, bettingService: null!, sqlInitService: null!, userService: null!, forzaService: null!);

    [Fact]
    public async Task 管理员发送关闭地平线_应写入false并回复地平线模块已关闭()
    {
        string original = PluginConfig.RootPath;
        string tmp = Path.Combine(Path.GetTempPath(), "candybot-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            PluginConfig.RootPath = tmp;

            RecordingBotReplySender sender = new();
            MemoryCache cache = new(new MemoryCacheOptions());
            const string openid = "admin-openid";
            cache.Set($"bot::{openid}", TestData.CreateUser(isAdmin: true, openid: openid), TimeSpan.FromMinutes(10));

            CandyBotFunctionConfig cfg = new()
            {
                System = true,
                ForzaHorizon = true,
                Config = new PluginConfig("candybot", "functions"),
            };

            CandyBotService svc = CreateService(sender, cfg, cache);
            C2CMessage e = TestData.CreateMessage("关闭地平线", openid);

            await svc.Handler(e);

            cfg.ForzaHorizon.Should().BeFalse("「关闭地平线」应将 ForzaHorizon 开关置为 false");
            sender.TextContents.Should().ContainSingle().Which.Should().Be("地平线模块已关闭。");
        }
        finally
        {
            PluginConfig.RootPath = original;
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
        }
    }

    [Fact]
    public async Task 管理员发送开启赛事系统_应写入true并回复赛事系统已开启()
    {
        string original = PluginConfig.RootPath;
        string tmp = Path.Combine(Path.GetTempPath(), "candybot-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            PluginConfig.RootPath = tmp;

            RecordingBotReplySender sender = new();
            MemoryCache cache = new(new MemoryCacheOptions());
            const string openid = "admin-openid";
            cache.Set($"bot::{openid}", TestData.CreateUser(isAdmin: true, openid: openid), TimeSpan.FromMinutes(10));

            CandyBotFunctionConfig cfg = new()
            {
                System = true,
                Betting = false,
                Config = new PluginConfig("candybot", "functions"),
            };

            CandyBotService svc = CreateService(sender, cfg, cache);
            C2CMessage e = TestData.CreateMessage("开启赛事系统", openid);

            await svc.Handler(e);

            cfg.Betting.Should().BeTrue();
            sender.TextContents.Should().ContainSingle().Which.Should().Be("赛事系统已开启。");
        }
        finally
        {
            PluginConfig.RootPath = original;
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
        }
    }
}
