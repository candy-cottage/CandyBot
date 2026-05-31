using Milimoe.FunGame.Core.Api.Utility;

namespace CandyCottage.CandyBot.Utilities
{
    public class TencentDocsConfig
    {
        public Lock Lock { get; set; } = new();
        public string ClientID { get; set; } = "";
        public string AccessToken { get; set; } = "";
        public string OpenId { get; set; } = "";

        public PluginConfig Config { get; set; } = new("candybot", "tencent_docs");

        public void Init()
        {
            Config.LoadConfig();
            ClientID = Config.GetOrAdd("ClientID", ClientID) ?? "";
            AccessToken = Config.GetOrAdd("AccessToken", AccessToken) ?? "";
            OpenId = Config.GetOrAdd("OpenId", OpenId) ?? "";
            Config.SaveConfig();
        }

        public void Save()
        {
            lock (Lock)
            {
                Config.SaveConfig();
            }
        }
    }
}
