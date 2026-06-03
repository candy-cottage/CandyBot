using Milimoe.FunGame.Core.Api.Utility;

namespace CandyCottage.CandyBot.Utilities
{
    public class ForzaConfig
    {
        public Dictionary<string, string> DocsID { get; set; } = [];
        public IReadOnlyList<string> Manufacturers => [.. _manufacturers];

        private readonly HashSet<string> _manufacturers = []; // runtime write only
        private readonly Lock _lock = new();

        public PluginConfig Config { get; set; } = new("candybot", "forza");

        public void Init()
        {
            Config.LoadConfig();
            DocsID = Config.GetOrAdd("DocsID", DocsID) ?? [];
            Config.SaveConfig();
        }

        public void Update(IEnumerable<string> manufacturers)
        {
            _lock.Enter();
            foreach (string m in manufacturers)
            {
                _manufacturers.Add(m);
            }
            _lock.Exit();
        }
    }
}
