using Milimoe.FunGame.Core.Api.Utility;
using Milimoe.FunGame.Core.Entity;

namespace CandyCottage.CandyBot.Modules
{
    public class CharacterModule : Milimoe.FunGame.Core.Library.Common.Addon.CharacterModule
    {
        public override string Name => ModuleConstant.CharacterModuleName;

        public override string Description => ModuleConstant.ModuleDescription;

        public override string Version => ModuleConstant.ModuleVersion;

        public override string Author => ModuleConstant.ModuleAuthor;

        public override Dictionary<string, Character> Characters
        {
            get
            {
                if (field != null) return field;
                field = [];
                foreach (Func<Character> func in CharacterGenerators.Values)
                {
                    Character character = func();
                    field[character.Name] = character;
                }
                return field;
            }
        }

        public Dictionary<long, Func<Character>> CharacterGenerators { get; } = [];

        protected override Factory.EntityFactoryDelegate<Character> CharacterFactory()
        {
            return (id, name, args) =>
            {
                if (CharacterGenerators.TryGetValue(id, out Func<Character>? func))
                {
                    return func();
                }
                return null;
            };
        }
    }
}
