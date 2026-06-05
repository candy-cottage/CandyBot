using CandyCottage.CandyBot.Modules.Skills.Magic;
using CandyCottage.CandyBot.Modules.Skills.Super;
using Milimoe.FunGame.Core.Library.Constant;

namespace CandyCottage.CandyBot.Modules.Characters
{
    public class Mili : CandyCottageCharacter
    {
        public override long Id => (long)CharacterID.Mili;

        public Mili()
        {
            InitialHP = 100;
            InitialMP = 50;
            InitialHR = 10;
            InitialMR = 5;
            InitialATK = 30;
            InitialDEF = 20;
            InitialSPD = 300;
            InitialSTR = 15;
            InitialAGI = 25;
            InitialINT = 20;
            PrimaryAttribute = PrimaryAttribute.AGI;

            Skills.Add(new RecoveryMagic(this));
            Skills.Add(new CrazyDamage(this));
        }
    }
}
