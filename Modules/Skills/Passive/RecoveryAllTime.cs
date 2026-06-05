using Milimoe.FunGame.Core.Entity;
using Milimoe.FunGame.Core.Library.Constant;

namespace CandyCottage.CandyBot.Modules.Skills.Passive
{
    public class RecoveryAllTime : Skill
    {
        public override long Id => (long)SkillID.RecoveryAllTime;
        public override string Name => "时刻治愈";

        public RecoveryAllTime(Character? character = null) : base(SkillType.Magic, character)
        {
            Effects.Add(new RecoveryMagicEffect(this));
        }

        public override IEnumerable<Effect> AddPassiveEffectToCharacter()
        {
            return Effects;
        }
    }

    public class RecoveryMagicEffect(Skill skill) : Effect(skill)
    {
        public override void OnTimeElapsed(Character character, double elapsed)
        {
            HealToTarget(character, character, elapsed * 5);
        }
    }
}
