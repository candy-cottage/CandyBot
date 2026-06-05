using Milimoe.FunGame.Core.Entity;
using Milimoe.FunGame.Core.Library.Common.Addon;
using Milimoe.FunGame.Core.Library.Constant;

namespace CandyCottage.CandyBot.Modules.Skills.Magic
{
    public class RecoveryMagic : Skill
    {
        public override long Id => (long)SkillID.RecoveryMagic;
        public override string Name => "回复术";
        public override double MPCost => 50;
        public override double CastTime => 2;
        public override double CD => 12;
        public override bool SelectAllEnemies => false;
        public override bool SelectAllTeammates => true;
        public override bool CanSelectSelf => true;

        public RecoveryMagic(Character? character = null) : base(SkillType.Magic, character)
        {
            Effects.Add(new RecoveryMagicEffect(this));
        }
    }

    public class RecoveryMagicEffect(Skill skill) : Effect(skill)
    {
        public override void OnSkillCasted(Character caster, List<Character> targets, List<Grid> grids, Dictionary<string, object> others)
        {
            foreach (Character target in targets)
            {
                HealToTarget(caster, target, 200);
            }
        }
    }
}
