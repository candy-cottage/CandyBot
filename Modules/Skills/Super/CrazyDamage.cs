using Milimoe.FunGame.Core.Entity;
using Milimoe.FunGame.Core.Library.Common.Addon;
using Milimoe.FunGame.Core.Library.Constant;

namespace CandyCottage.CandyBot.Modules.Skills.Super
{
    public class CrazyDamage : Skill
    {
        public override long Id => (long)SkillID.CrazyDamage;
        public override string Name => "疯狂伤害";
        public override double CD => 24;

        public CrazyDamage(Character? character = null) : base(SkillType.SuperSkill, character)
        {
            Effects.Add(new CrazyDamageEffect(this));
        }
    }

    public class CrazyDamageEffect(Skill skill) : Effect(skill)
    {
        public override void OnSkillCasted(Character caster, List<Character> targets, List<Grid> grids, Dictionary<string, object> others)
        {
            foreach (Character target in targets)
            {
                DamageToEnemy(caster, target, DamageType.Magical, MagicType, 400);
            }
        }
    }
}
