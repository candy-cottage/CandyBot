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

        public void Init()
        {
            QQBotConfig.LoadConfig();
            AppId = QQBotConfig.GetOrAdd("AppId", AppId) ?? "";
            Secret = QQBotConfig.GetOrAdd("Secret", Secret) ?? "";
            OpenId = QQBotConfig.GetOrAdd("OpenId", OpenId) ?? "";
            UseSandbox = QQBotConfig.GetOrAdd("UseSandbox", UseSandbox);
            QQBotConfig.SaveConfig();
        }
    }
}
