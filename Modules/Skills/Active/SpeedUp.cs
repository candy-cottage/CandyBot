using Milimoe.FunGame.Core.Entity;
using Milimoe.FunGame.Core.Library.Common.Addon;
using Milimoe.FunGame.Core.Library.Constant;

namespace CandyCottage.CandyBot.Modules.Skills.Active
{
    public class SpeedUp : Skill
    {
        public override long Id => (long)SkillID.SpeedUp;
        public override string Name => "加速";
        public override double CD => 10;

        public SpeedUp(Character? character = null) : base(SkillType.Magic, character)
        {
            Effects.Add(new SpeedUpEffect(this));
        }
    }

    public class SpeedUpEffect(Skill skill) : Effect(skill)
    {
        public override void OnSkillCasted(Character caster, List<Character> targets, List<Grid> grids, Dictionary<string, object> others)
        {
            foreach (Character target in targets)
            {
                // todo
            }
        }
    }
}
