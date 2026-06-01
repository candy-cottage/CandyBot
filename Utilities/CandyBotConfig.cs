using Milimoe.FunGame.Core.Api.Utility;

namespace CandyCottage.CandyBot.Utilities
{
    public class CandyBotConfig
    {
        public string ApiTokenSalt { get; set; } = "1_2+3!4?5*6";

        public PluginConfig Config { get; set; } = new("candybot", "candybot");

        /// <summary>
        /// 优先级：环境变量 &gt; PluginConfig &gt; 默认值。环境变量：CANDYBOT_APITOKENSALT。
        /// 环境变量覆盖仅作用于内存，不写回配置文件。
        /// </summary>
        public void Init()
        {
            Config.LoadConfig();
            ApiTokenSalt = Config.GetOrAdd("ApiTokenSalt", ApiTokenSalt) ?? "";
            Config.SaveConfig();

            // 环境变量覆盖（在 SaveConfig 之后，仅内存生效，不持久化注入的机密）。
            ApiTokenSalt = ConfigEnv.Override("CANDYBOT_APITOKENSALT", ApiTokenSalt);
        }
    }
}
