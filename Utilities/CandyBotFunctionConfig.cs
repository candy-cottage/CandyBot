using Milimoe.FunGame.Core.Api.Utility;

namespace CandyCottage.CandyBot.Utilities
{
    public class CandyBotFunctionConfig
    {
        public Lock Lock { get; } = new();
        public bool System { get; set; } = true;
        public bool AllowAdminsDebug { get; set; } = true;
        public bool CandyCottage { get; set; } = true;
        public bool UserSaved { get; set; } = true;
        public bool UserRename { get; set; } = true;
        public bool ForzaHorizon { get; set; } = true;
        public bool Betting { get; set; } = true;
        public bool Betting_ReadOnly { get; set; } = false;
        public bool BettingAutomation { get; set; } = false;

        public Dictionary<string, (Action<CandyBotFunctionConfig, bool> Setter, string Description)> ToggleConfigs { get; } = new()
        {
            { "机器人", ((config, value) => { config.System = value; config.Config["System"] = value; }, "全部系统/模块/功能") },
            { "管理员调试", ((config, value) => { config.AllowAdminsDebug = value; config.Config["AllowAdminsDebug"] = value; }, "管理员在系统/模块/功能关闭期间可调试") },
            { "糖果屋", ((config, value) => { config.CandyCottage = value; config.Config["CandyCottage"] = value; }, "糖果屋模块") },
            { "用户系统", ((config, value) => { config.UserSaved = value; config.Config["UserSaved"] = value; }, "用户系统") },
            { "昵称修改", ((config, value) => { config.UserRename = value; config.Config["UserRename"] = value; }, "昵称修改功能") },
            { "地平线", ((config, value) => { config.ForzaHorizon = value; config.Config["ForzaHorizon"] = value; }, "地平线模块") },
            { "赛事系统", ((config, value) => { config.Betting = value; config.Config["Betting"] = value; }, "赛事系统") },
            { "赛事只读", ((config, value) => { config.Betting_ReadOnly = value; config.Config["Betting_ReadOnly"] = value; }, "赛事只读功能") },
            { "赛事自动化", ((config, value) => { config.BettingAutomation = value; config.Config["BettingAutomation"] = value; }, "赛事自动化") }
        };

        public PluginConfig Config { get; set; } = new("candybot", "functions");

        public void Init()
        {
            Config.LoadConfig();
            System = Config.GetOrAdd("System", System);
            AllowAdminsDebug = Config.GetOrAdd("AllowAdminsDebug", AllowAdminsDebug);
            CandyCottage = Config.GetOrAdd("CandyCottage", CandyCottage);
            UserSaved = Config.GetOrAdd("UserSaved", UserSaved);
            UserRename = Config.GetOrAdd("UserRename", UserRename);
            ForzaHorizon = Config.GetOrAdd("ForzaHorizon", ForzaHorizon);
            Betting = Config.GetOrAdd("Betting", Betting);
            Betting_ReadOnly = Config.GetOrAdd("Betting_ReadOnly", Betting_ReadOnly);
            BettingAutomation = Config.GetOrAdd("BettingAutomation", BettingAutomation);
            Config.SaveConfig();
        }
    }
}
