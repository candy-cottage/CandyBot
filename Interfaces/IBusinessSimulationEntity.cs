namespace CandyCottage.CandyBot.Interfaces
{
    public interface IBusinessSimulationEntity
    {
        public string Category { get; }
        public bool Enable { get; }
        public Dictionary<string, string> SkillInfo { get; }

        public void UpdateSkillInfo();
    }
}
