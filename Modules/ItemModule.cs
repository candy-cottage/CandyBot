using Milimoe.FunGame.Core.Api.OpenEntityAdapter;
using Milimoe.FunGame.Core.Api.Utility;
using Milimoe.FunGame.Core.Entity;

namespace CandyCottage.CandyBot.Modules
{
    public class ItemModule : Milimoe.FunGame.Core.Library.Common.Addon.ItemModule
    {
        public override string Name => ModuleConstant.ItemModuleName;

        public override string Description => ModuleConstant.ModuleDescription;

        public override string Version => ModuleConstant.ModuleVersion;

        public override string Author => ModuleConstant.ModuleAuthor;

        public override Dictionary<string, Item> Items
        {
            get
            {
                if (field != null) return field;
                field = [];
                foreach (Func<Item> func in ItemGenerators.Values)
                {
                    Item item = func();
                    field[item.Name] = item;
                }
                EntityModuleConfig<Item> extendItems = new("candy-cottage", "extend-items");
                extendItems.LoadConfig();
                OpenItemAdapter.Adaptation(extendItems);
                foreach (string key in extendItems.Keys)
                {
                    field[key] = extendItems[key];
                }
                return field;
            }
        }

        public Dictionary<long, Func<Item>> ItemGenerators { get; } = [];

        protected override Factory.EntityFactoryDelegate<Item> ItemFactory()
        {
            return (id, name, args) =>
            {
                if (ItemGenerators.TryGetValue(id, out Func<Item>? func))
                {
                    return func();
                }
                return null;
            };
        }
    }
}
