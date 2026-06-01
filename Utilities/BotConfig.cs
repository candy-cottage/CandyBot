using Milimoe.FunGame.Core.Api.Utility;

namespace CandyCottage.CandyBot.Utilities
{
    public class BotConfig
    {
        public string AppId { get; set; } = "11111111";
        public string Secret { get; set; } = "DG5g3B4j9X2KOErG";
        public string OpenId { get; set; } = "";
        public bool UseSandbox { get; set; } = false;
        public string GatewayHost => UseSandbox ? "https://sandbox.api.sgroup.qq.com" : "https://api.sgroup.qq.com";

        public PluginConfig QQBotConfig { get; set; } = new("candybot", "bot");

        /// <summary>
        /// 优先级：环境变量 &gt; PluginConfig &gt; 默认值。
        /// 环境变量：CANDYBOT_BOT_APPID / CANDYBOT_BOT_SECRET / CANDYBOT_BOT_OPENID / CANDYBOT_BOT_USESANDBOX。
        /// 环境变量覆盖仅作用于内存，不写回配置文件。
        /// </summary>
        public void Init()
        {
            QQBotConfig.LoadConfig();
            AppId = QQBotConfig.GetOrAdd("AppId", AppId) ?? "";
            Secret = QQBotConfig.GetOrAdd("Secret", Secret) ?? "";
            OpenId = QQBotConfig.GetOrAdd("OpenId", OpenId) ?? "";
            UseSandbox = QQBotConfig.GetOrAdd("UseSandbox", UseSandbox);
            QQBotConfig.SaveConfig();

            // 环境变量覆盖（在 SaveConfig 之后，仅内存生效，不持久化注入的机密）。
            AppId = ConfigEnv.Override("CANDYBOT_BOT_APPID", AppId);
            Secret = ConfigEnv.Override("CANDYBOT_BOT_SECRET", Secret);
            OpenId = ConfigEnv.Override("CANDYBOT_BOT_OPENID", OpenId);
            UseSandbox = ConfigEnv.Override("CANDYBOT_BOT_USESANDBOX", UseSandbox);
        }
    }
}
