using Milimoe.FunGame.Core.Api.Utility;

namespace CandyCottage.CandyBot.Utilities
{
    public static class PluginConfigExtension
    {
        public static T? GetOrAdd<T>(this PluginConfig config, string key, T? defaultValue)
        {
            if (!config.ContainsKey(key) && defaultValue != null)
            {
                config[key] = defaultValue;
                return defaultValue;
            }
            return config.Get<T>(key);
        }
    }
}
