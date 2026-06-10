using Milimoe.FunGame.Core.Api.Utility;

namespace CandyCottage.CandyBot.Utilities
{
    public class ForzaConfig
    {
        public Dictionary<string, string> DocsID { get; set; } = [];

        /// <summary>制造商缓存快照。读取必须与 <see cref="Update"/>/<see cref="Invalidate"/> 互斥：本类是单例，定时同步线程写入时消息处理线程可能正在读取。</summary>
        public IReadOnlyList<string> Manufacturers
        {
            get
            {
                lock (_lock)
                {
                    return [.. _manufacturers];
                }
            }
        }

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
            // lock 语句保证枚举中途抛异常时锁也能释放（手动 Enter/Exit 会永久持锁）
            lock (_lock)
            {
                foreach (string m in manufacturers)
                {
                    _manufacturers.Add(m);
                }
            }
        }

        /// <summary>
        /// 车辆数据发生写入（新增/修改/删除）后调用：清空制造商缓存。
        /// <see cref="Update"/> 只增不减，无法处理制造商被删除/改名的情况；
        /// 清空后下一次组合搜索会从数据库重建缓存。
        /// </summary>
        public void Invalidate()
        {
            lock (_lock)
            {
                _manufacturers.Clear();
            }
        }
    }
}
