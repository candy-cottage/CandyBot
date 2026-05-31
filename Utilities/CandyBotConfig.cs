using Milimoe.FunGame.Core.Api.Utility;

namespace CandyCottage.CandyBot.Utilities
{
    public class CandyBotConfig
    {
        public string ApiTokenSalt { get; set; } = "1_2+3!4?5*6";

        public PluginConfig Config { get; set; } = new("candybot", "candybot");

        public void Init()
        {
            Config.LoadConfig();
            ApiTokenSalt = Config.GetOrAdd("ApiTokenSalt", ApiTokenSalt) ?? "";
            Config.SaveConfig();
        }
    }
}
