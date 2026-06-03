using CandyCottage.CandyBot.Utilities;
using FluentAssertions;
using Milimoe.FunGame.Core.Api.Utility;
using Xunit;

namespace CandyBot.Tests.Utilities;

/// <summary>
/// B15：CandyBotFunctionConfig.Init 应从各自对应的键回读每个开关。
/// 历史 bug：AllowAdminsDebug 误从 "CandyCottage" 键读取。
///
/// 隔离方式：PluginConfig.RootPath 是进程级静态全局，指向临时目录后，配置文件
/// （configs/candybot/functions.json）写入临时位置，避免污染开发者真实配置；
/// 测试结束恢复原值并清理目录。由于静态状态共享，本类放入禁用并行的测试集合。
/// </summary>
[Collection(PluginConfigCollection.Name)]
public class CandyBotFunctionConfigTests
{
    private const string PluginName = "candybot";
    private const string FileName = "functions";

    private static T WithTempRootPath<T>(Func<string, T> body)
    {
        string original = PluginConfig.RootPath;
        string tmp = Path.Combine(Path.GetTempPath(), "candybot-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            PluginConfig.RootPath = tmp;
            return body(tmp);
        }
        finally
        {
            PluginConfig.RootPath = original;
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
        }
    }

    /// <summary>把一组开关值持久化到磁盘后备存储，供随后 Init 回读。</summary>
    private static void SeedPersistedConfig(IDictionary<string, bool> values)
    {
        PluginConfig seed = new(PluginName, FileName);
        seed.LoadConfig();
        foreach ((string key, bool value) in values)
        {
            seed[key] = value;
        }
        seed.SaveConfig();
    }

    [Fact]
    public void Init_应从各自的键回读每个开关_AllowAdminsDebug不再误读CandyCottage()
    {
        WithTempRootPath<object?>(_ =>
        {
            // 让 AllowAdminsDebug 与 CandyCottage 取不同的值，从而能区分二者是否来自各自的键。
            SeedPersistedConfig(new Dictionary<string, bool>
            {
                ["System"] = false,
                ["AllowAdminsDebug"] = false,
                ["CandyCottage"] = true,
                ["UserSaved"] = false,
                ["UserRename"] = false,
                ["ForzaHorizon"] = false,
                ["Betting"] = false,
                ["Betting_ReadOnly"] = true,
                ["BettingAutomation"] = false,
            });

            CandyBotFunctionConfig config = new();
            config.Init();

            // 每个字段应来自其同名键。
            config.System.Should().BeFalse();
            config.AllowAdminsDebug.Should().BeFalse("AllowAdminsDebug 应从 \"AllowAdminsDebug\" 键读取，而非 \"CandyCottage\"");
            config.CandyCottage.Should().BeTrue();
            config.UserSaved.Should().BeFalse();
            config.UserRename.Should().BeFalse();
            config.ForzaHorizon.Should().BeFalse();
            config.Betting.Should().BeFalse();
            config.Betting_ReadOnly.Should().BeTrue();
            config.BettingAutomation.Should().BeFalse();

            return null;
        });
    }

    [Fact]
    public void Init_当AllowAdminsDebug为true而CandyCottage为false时应分别回读()
    {
        // 与上一个测试相反的取值组合，进一步证明二者不再共用同一个键。
        WithTempRootPath<object?>(_ =>
        {
            SeedPersistedConfig(new Dictionary<string, bool>
            {
                ["AllowAdminsDebug"] = true,
                ["CandyCottage"] = false,
            });

            CandyBotFunctionConfig config = new();
            config.Init();

            config.AllowAdminsDebug.Should().BeTrue();
            config.CandyCottage.Should().BeFalse();

            return null;
        });
    }
}

[CollectionDefinition(PluginConfigCollection.Name, DisableParallelization = true)]
public class PluginConfigCollection
{
    public const string Name = "PluginConfig static RootPath";
}
