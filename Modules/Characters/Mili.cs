using CandyCottage.CandyBot.Modules.Skills.Mili;

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

            Skills.Add(new RecoveryMagic(this));
        }
    }
}
