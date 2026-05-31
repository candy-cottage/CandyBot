using Milimoe.FunGame.Core.Api.Utility;
using Milimoe.FunGame.Core.Entity;

namespace CandyCottage.CandyBot.Modules
{
    public class SkillModule : Milimoe.FunGame.Core.Library.Common.Addon.SkillModule
    {
        public override string Name => ModuleConstant.SkillModuleName;

        public override string Description => ModuleConstant.ModuleDescription;

        public override string Version => ModuleConstant.ModuleVersion;

        public override string Author => ModuleConstant.ModuleAuthor;

        public override Dictionary<string, Skill> Skills
        {
            get
            {
                if (field != null) return field;
                field = [];
                foreach (Func<Skill> func in SkillGenerators.Values)
                {
                    Skill skill = func();
                    field[skill.Name] = skill;
                }
                return field;
            }
        }

        public Dictionary<long, Func<Skill>> SkillGenerators { get; } = [];

        protected override Factory.EntityFactoryDelegate<Skill> SkillFactory()
        {
            return (id, name, args) =>
            {
                if (SkillGenerators.TryGetValue(id, out Func<Skill>? func))
                {
                    return func();
                }
                return null;
            };
        }

        public Dictionary<long, Func<Effect>> EffectGenerators { get; } = [];

        protected override Factory.EntityFactoryDelegate<Effect> EffectFactory()
        {
            return (id, name, args) =>
            {
                if (EffectGenerators.TryGetValue(id, out Func<Effect>? func))
                {
                    return func();
                }
                return null;
            };
        }
    }
}
