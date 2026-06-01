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

        /// <summary>
        /// 优先级：环境变量 &gt; PluginConfig &gt; 默认值。
        /// 环境变量：CANDYBOT_TENCENTDOCS_CLIENTID / CANDYBOT_TENCENTDOCS_ACCESSTOKEN / CANDYBOT_TENCENTDOCS_OPENID。
        /// 环境变量覆盖仅作用于内存，不写回配置文件。
        /// </summary>
        public void Init()
        {
            Config.LoadConfig();
            ClientID = Config.GetOrAdd("ClientID", ClientID) ?? "";
            AccessToken = Config.GetOrAdd("AccessToken", AccessToken) ?? "";
            OpenId = Config.GetOrAdd("OpenId", OpenId) ?? "";
            Config.SaveConfig();

            // 环境变量覆盖（在 SaveConfig 之后，仅内存生效，不持久化注入的机密）。
            ClientID = ConfigEnv.Override("CANDYBOT_TENCENTDOCS_CLIENTID", ClientID);
            AccessToken = ConfigEnv.Override("CANDYBOT_TENCENTDOCS_ACCESSTOKEN", AccessToken);
            OpenId = ConfigEnv.Override("CANDYBOT_TENCENTDOCS_OPENID", OpenId);
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
