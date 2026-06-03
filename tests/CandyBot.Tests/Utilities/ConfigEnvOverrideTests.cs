using CandyCottage.CandyBot.Utilities;
using FluentAssertions;
using Milimoe.FunGame.Core.Api.Utility;
using Milimoe.FunGame.Core.Library.Constant;
using Xunit;

namespace CandyBot.Tests.Utilities;

/// <summary>
/// 验证配置类的环境变量覆盖：优先级为 环境变量 &gt; PluginConfig &gt; 默认值，
/// 且环境变量覆盖仅作用于内存、不写回配置文件。
///
/// 隔离方式：环境变量与 <see cref="PluginConfig.RootPath"/> 均为进程级全局状态，
/// 因此本类与 <see cref="CandyBotFunctionConfigTests"/> 同属禁用并行的测试集合，
/// 并在 try/finally 中设置 / 清理 RootPath 与各环境变量。
/// </summary>
[Collection(PluginConfigCollection.Name)]
public class ConfigEnvOverrideTests
{
    /// <summary>在临时 RootPath 与指定环境变量下执行 body，结束后恢复并清理全部状态。</summary>
    private static void WithEnvAndTempRoot(IDictionary<string, string?> envVars, Action body)
    {
        string originalRoot = PluginConfig.RootPath;
        string tmp = Path.Combine(Path.GetTempPath(), "candybot-env-test-" + Guid.NewGuid().ToString("N"));
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
            body();
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
    public void CandyBotConfig_环境变量优先于持久化值()
    {
        WithEnvAndTempRoot(new Dictionary<string, string?> { ["CANDYBOT_APITOKENSALT"] = "from-env" }, () =>
        {
            // 先在 PluginConfig 写入一个不同的值，证明环境变量优先级更高。
            PluginConfig seed = new("candybot", "candybot");
            seed.LoadConfig();
            seed["ApiTokenSalt"] = "from-pluginconfig";
            seed.SaveConfig();

            CandyBotConfig config = new();
            config.Init();

            config.ApiTokenSalt.Should().Be("from-env", "环境变量应优先于 PluginConfig 持久化值");
        });
    }

    [Fact]
    public void CandyBotConfig_环境变量覆盖不写回配置文件()
    {
        WithEnvAndTempRoot(new Dictionary<string, string?> { ["CANDYBOT_APITOKENSALT"] = "from-env" }, () =>
        {
            PluginConfig seed = new("candybot", "candybot");
            seed.LoadConfig();
            seed["ApiTokenSalt"] = "from-pluginconfig";
            seed.SaveConfig();

            CandyBotConfig config = new();
            config.Init();

            // 重新从磁盘加载：持久化值应仍是 PluginConfig 原值，环境变量注入的机密未落盘。
            PluginConfig reloaded = new("candybot", "candybot");
            reloaded.LoadConfig();
            string persisted = reloaded.GetOrAdd("ApiTokenSalt", "") ?? "";
            persisted.Should().Be("from-pluginconfig", "环境变量覆盖应仅作用于内存，不应写回配置文件");
        });
    }

    [Fact]
    public void CandyBotConfig_环境变量未设置时回退到持久化值()
    {
        WithEnvAndTempRoot(new Dictionary<string, string?> { ["CANDYBOT_APITOKENSALT"] = null }, () =>
        {
            PluginConfig seed = new("candybot", "candybot");
            seed.LoadConfig();
            seed["ApiTokenSalt"] = "from-pluginconfig";
            seed.SaveConfig();

            CandyBotConfig config = new();
            config.Init();

            config.ApiTokenSalt.Should().Be("from-pluginconfig", "环境变量未设置时应回退到 PluginConfig 值");
        });
    }

    [Fact]
    public void CandyBotConfig_环境变量为空白时不覆盖()
    {
        WithEnvAndTempRoot(new Dictionary<string, string?> { ["CANDYBOT_APITOKENSALT"] = "   " }, () =>
        {
            PluginConfig seed = new("candybot", "candybot");
            seed.LoadConfig();
            seed["ApiTokenSalt"] = "from-pluginconfig";
            seed.SaveConfig();

            CandyBotConfig config = new();
            config.Init();

            config.ApiTokenSalt.Should().Be("from-pluginconfig", "空白环境变量不应覆盖（避免误清空配置）");
        });
    }

    [Fact]
    public void SQLConnectionConfig_Mode枚举大小写不敏感解析()
    {
        // 默认 Mode 为 SQLite；环境变量用小写 "mysql" 也应正确解析为 SQLMode.MySQL。
        WithEnvAndTempRoot(new Dictionary<string, string?>
        {
            ["CANDYBOT_SQL_MODE"] = "mysql",
            ["CANDYBOT_SQL_PASSWORD"] = "env-secret",
        }, () =>
        {
            SQLConnectionConfig config = new();
            config.Init();

            config.Mode.Should().Be(SQLMode.MySQL, "CANDYBOT_SQL_MODE 应大小写不敏感地解析为枚举");
            config.Password.Should().Be("env-secret", "字符串型机密应由环境变量覆盖");
        });
    }

    [Fact]
    public void SQLConnectionConfig_Password环境变量优先于持久化值()
    {
        WithEnvAndTempRoot(new Dictionary<string, string?> { ["CANDYBOT_SQL_PASSWORD"] = "env-secret" }, () =>
        {
            PluginConfig seed = new("sqlconfig", "connection");
            seed.LoadConfig();
            seed["Password"] = "disk-secret";
            seed.SaveConfig();

            SQLConnectionConfig config = new();
            config.Init();

            config.Password.Should().Be("env-secret", "环境变量应优先于 PluginConfig 持久化的密码");

            // 落盘值不应被环境变量污染。
            PluginConfig reloaded = new("sqlconfig", "connection");
            reloaded.LoadConfig();
            (reloaded.GetOrAdd("Password", "") ?? "").Should().Be("disk-secret", "环境变量覆盖不应写回配置文件");
        });
    }

    [Fact]
    public void BotConfig_UseSandbox布尔环境变量正确解析()
    {
        // 默认 UseSandbox=false；环境变量 "true" 应解析为 true。
        WithEnvAndTempRoot(new Dictionary<string, string?> { ["CANDYBOT_BOT_USESANDBOX"] = "true" }, () =>
        {
            BotConfig config = new();
            config.Init();

            config.UseSandbox.Should().BeTrue("CANDYBOT_BOT_USESANDBOX=\"true\" 应解析为 true");
        });
    }

    [Fact]
    public void BotConfig_UseSandbox非法布尔值时不覆盖()
    {
        // 非法值无法解析为 bool，应保留 PluginConfig / 默认值（默认 false）。
        WithEnvAndTempRoot(new Dictionary<string, string?> { ["CANDYBOT_BOT_USESANDBOX"] = "notabool" }, () =>
        {
            BotConfig config = new();
            config.Init();

            config.UseSandbox.Should().BeFalse("无法解析为 bool 的环境变量不应覆盖原值");
        });
    }

    [Fact]
    public void DeepSeekConfig_环境变量优先于持久化值且不写回()
    {
        WithEnvAndTempRoot(new Dictionary<string, string?>
        {
            ["CANDYBOT_DEEPSEEK_APIKEY"] = "env-key",
            ["CANDYBOT_DEEPSEEK_MODEL"] = "deepseek-v4-pro",
            ["CANDYBOT_DEEPSEEK_ENABLED"] = "true",
        }, () =>
        {
            PluginConfig seed = new("candybot", "deepseek");
            seed.LoadConfig();
            seed["ApiKey"] = "disk-key";
            seed["Model"] = "deepseek-v4-flash";
            seed["Enabled"] = false;
            seed.SaveConfig();

            DeepSeekConfig config = new();
            config.Init();

            config.ApiKey.Should().Be("env-key");
            config.Model.Should().Be("deepseek-v4-pro");
            config.Enabled.Should().BeTrue();

            PluginConfig reloaded = new("candybot", "deepseek");
            reloaded.LoadConfig();
            (reloaded.GetOrAdd("ApiKey", "") ?? "").Should().Be("disk-key");
        });
    }

    [Fact]
    public void PandaScoreConfig_环境变量优先于持久化值且不写回()
    {
        WithEnvAndTempRoot(new Dictionary<string, string?>
        {
            ["CANDYBOT_PANDASCORE_APIKEY"] = "env-key",
            ["CANDYBOT_PANDASCORE_BASEURL"] = "https://api.pandascore.co",
            ["CANDYBOT_PANDASCORE_GAMESLUG"] = "csgo",
            ["CANDYBOT_PANDASCORE_SCHEDULEPOLLINTERVALMINUTES"] = "30",
            ["CANDYBOT_PANDASCORE_RESULTPOLLINTERVALMINUTES"] = "5",
        }, () =>
        {
            PluginConfig seed = new("candybot", "pandascore");
            seed.LoadConfig();
            seed["ApiKey"] = "disk-key";
            seed.SaveConfig();

            PandaScoreConfig config = new();
            config.Init();

            config.ApiKey.Should().Be("env-key");
            config.BaseUrl.Should().Be("https://api.pandascore.co");
            config.GameSlug.Should().Be("csgo");
            config.SchedulePollInterval.Should().Be(TimeSpan.FromMinutes(30));
            config.ResultPollInterval.Should().Be(TimeSpan.FromMinutes(5));

            PluginConfig reloaded = new("candybot", "pandascore");
            reloaded.LoadConfig();
            (reloaded.GetOrAdd("ApiKey", "") ?? "").Should().Be("disk-key");
        });
    }
}
