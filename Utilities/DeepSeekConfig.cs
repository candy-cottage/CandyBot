using Milimoe.FunGame.Core.Api.Utility;

namespace CandyCottage.CandyBot.Utilities;

public class DeepSeekConfig
{
    public bool Enabled { get; set; } = false;
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.deepseek.com";
    public string Model { get; set; } = "deepseek-v4-flash";

    public PluginConfig Config { get; set; } = new("candybot", "deepseek");

    /// <summary>
    /// 优先级：环境变量 &gt; PluginConfig &gt; 默认值。环境变量：
    /// CANDYBOT_DEEPSEEK_ENABLED(bool) / CANDYBOT_DEEPSEEK_APIKEY / CANDYBOT_DEEPSEEK_BASEURL / CANDYBOT_DEEPSEEK_MODEL。
    /// 环境变量覆盖仅作用于内存，不写回配置文件。
    /// </summary>
    public void Init()
    {
        Config.LoadConfig();
        Enabled = Config.GetOrAdd("Enabled", Enabled);
        ApiKey = Config.GetOrAdd("ApiKey", ApiKey) ?? "";
        BaseUrl = Config.GetOrAdd("BaseUrl", BaseUrl) ?? "";
        Model = Config.GetOrAdd("Model", Model) ?? "";
        Config.SaveConfig();

        // 环境变量覆盖（在 SaveConfig 之后，仅内存生效，不持久化注入的机密）。
        Enabled = ConfigEnv.Override("CANDYBOT_DEEPSEEK_ENABLED", Enabled);
        ApiKey = ConfigEnv.Override("CANDYBOT_DEEPSEEK_APIKEY", ApiKey);
        BaseUrl = ConfigEnv.Override("CANDYBOT_DEEPSEEK_BASEURL", BaseUrl);
        Model = ConfigEnv.Override("CANDYBOT_DEEPSEEK_MODEL", Model);
    }
}
