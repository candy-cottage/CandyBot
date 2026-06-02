namespace CandyCottage.CandyBot.Utilities
{
    /// <summary>
    /// 配置项的环境变量覆盖辅助。优先级：<b>环境变量 &gt; PluginConfig 持久化值 &gt; 硬编码默认值</b>。
    /// 仅当环境变量已设置且非空白时才覆盖；覆盖只发生在内存中（不写回 PluginConfig 文件），
    /// 因此通过环境变量注入的机密不会被持久化到磁盘。
    ///
    /// 支持的环境变量（统一 <c>CANDYBOT_</c> 前缀，全大写）：
    /// <list type="bullet">
    ///   <item><c>CANDYBOT_BOT_APPID</c> / <c>CANDYBOT_BOT_SECRET</c> / <c>CANDYBOT_BOT_OPENID</c> / <c>CANDYBOT_BOT_USESANDBOX</c>(bool)</item>
    ///   <item><c>CANDYBOT_TENCENTDOCS_CLIENTID</c> / <c>CANDYBOT_TENCENTDOCS_ACCESSTOKEN</c> / <c>CANDYBOT_TENCENTDOCS_OPENID</c></item>
    ///   <item><c>CANDYBOT_SQL_MODE</c>(enum) / <c>CANDYBOT_SQL_DATASOURCE</c> / <c>CANDYBOT_SQL_PORT</c> / <c>CANDYBOT_SQL_DATABASE</c> / <c>CANDYBOT_SQL_USER</c> / <c>CANDYBOT_SQL_PASSWORD</c></item>
    ///   <item><c>CANDYBOT_APITOKENSALT</c></item>
    ///   <item><c>CANDYBOT_DEEPSEEK_ENABLED</c>(bool) / <c>CANDYBOT_DEEPSEEK_APIKEY</c> / <c>CANDYBOT_DEEPSEEK_BASEURL</c> / <c>CANDYBOT_DEEPSEEK_MODEL</c></item>
    /// </list>
    /// </summary>
    public static class ConfigEnv
    {
        /// <summary>环境变量已设置且非空白时返回其值，否则返回 <paramref name="current"/>。</summary>
        public static string Override(string envName, string current)
        {
            string? value = Environment.GetEnvironmentVariable(envName);
            return string.IsNullOrWhiteSpace(value) ? current : value;
        }

        /// <summary>bool 覆盖：环境变量可解析为 bool 时返回解析值，否则返回 <paramref name="current"/>。</summary>
        public static bool Override(string envName, bool current)
        {
            string? value = Environment.GetEnvironmentVariable(envName);
            return !string.IsNullOrWhiteSpace(value) && bool.TryParse(value, out bool parsed) ? parsed : current;
        }

        /// <summary>枚举覆盖（大小写不敏感）：环境变量可解析为 <typeparamref name="TEnum"/> 时返回解析值，否则返回 <paramref name="current"/>。</summary>
        public static TEnum Override<TEnum>(string envName, TEnum current) where TEnum : struct, Enum
        {
            string? value = Environment.GetEnvironmentVariable(envName);
            return !string.IsNullOrWhiteSpace(value) && Enum.TryParse(value, true, out TEnum parsed) ? parsed : current;
        }
    }
}
