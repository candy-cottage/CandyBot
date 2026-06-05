using CandyCottage.CandyBot.Entities.HumanResource;

namespace CandyCottage.CandyBot.Entities.RealEstate
{
    public class ResidentialRealEstate : BaseRealEstate
    {
        public override RealEstateType RealEstateType => RealEstateType.Residential;

        public virtual int MaxResidents { get; set; } = 0;
        public virtual int ExperiencePerMinute { get; set; } = 0;
        public virtual double MoodRestoreBonus { get; set; } = 0;
        public virtual int ExtraInventoryCapacity { get; set; } = 0;
        public virtual double CostPerResident { get; set; } = 0;

        public override double MaintenanceCost => Characters.Count * CostPerResident;
        public override string MaintenanceUnit => "每人每日";
        public HashSet<BaseHumanResource> Characters { get; } = [];

        public ResidentialRealEstate()
        {
            UpdateSkillInfo();
        }

        public override void UpdateSkillInfo()
        {
            if (MaxResidents > 0) SkillInfo["可入住角色数量"] = $"{Characters.Count} / {MaxResidents}";
            if (ExperiencePerMinute > 0) SkillInfo["入住经验加成"] = $"{ExperiencePerMinute} / 人每分钟";
            if (MoodRestoreBonus > 0) SkillInfo["心情恢复速度提升"] = $"{MoodRestoreBonus * 100:0.##}%";
            if (ExtraInventoryCapacity > 0) SkillInfo["额外库存容量"] = $"{ExtraInventoryCapacity}";
            if (Characters.Count > 0) SkillInfo["已入住角色"] = $"\r\n{string.Join("\r\n", Characters.Select(c => c.ToStringWithLevelWithOutUser()))}";
        }
    }
}
