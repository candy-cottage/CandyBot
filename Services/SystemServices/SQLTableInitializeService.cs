using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Utilities;
using Milimoe.FunGame.Core.Api.Transmittal;
using Milimoe.FunGame.Core.Api.Utility;
using Milimoe.FunGame.Core.Entity;
using Milimoe.FunGame.Core.Library.Constant;
using Milimoe.FunGame.Core.Library.SQLScript.Common;
using ProjectRedbud.FunGame.SQLQueryExtension;

namespace CandyCottage.CandyBot.Services.SystemServices
{
    public class SQLTableInitializeService(ILogger<SQLTableInitializeService> logger, SQLHelper sql)
    {
        public bool WillInitialize()
        {
            PluginConfig initializationConfig = new("sqlconfig", "initialization");
            initializationConfig.LoadConfig();
            string serverAdmin = initializationConfig.Get<string>("admin") ?? "";
            return serverAdmin == "";
        }

        public void Initialize(string firstAdminOpenID, out string msg)
        {
            PluginConfig initializationConfig = new("sqlconfig", "initialization");
            initializationConfig.LoadConfig();
            try
            {
                sql.NewTransaction();
                if (sql is SQLiteHelper sqliteHelper)
                {
                    sqliteHelper.ExecuteSqlFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "fungame_sqlite.sql"));
                }
                else if (sql is MySQLHelper mysqlHelper)
                {
                    mysqlHelper.ExecuteSqlFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "fungame.sql"));
                }
                sql.ExecuteSqlFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "betting.sql"));
                sql.ExecuteSqlFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "candybot.sql"));
                sql.ExecuteSqlFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "forza.sql"));
                initializationConfig.Add("admin", firstAdminOpenID);
                initializationConfig.Add("time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                initializationConfig.SaveConfig();
                sql.Execute(Configs.Insert_Config(sql, "Initialization", FunGameInfo.FunGame_Version, "SQL Service Installed."));
                string username = firstAdminOpenID;
                string autokey = username.Encrypt(username);
                string password = Verification.CreateVerifyCode(VerifyCodeType.NumberVerifyCode, 6).Encrypt(username);
                sql.RegisterUser(username, password, "", "", autokey);
                User user = sql.GetUserByUsernameAndAutoKey(username, autokey, true) ?? throw new Exception("添加新用户失败，无法获取新用户数据。");
                user.IsAdmin = true;
                user.IsOperator = true;
                user.NickName = $"Candy#Admin";
                user.Inventory.Credits = 50000;
                user.Inventory.Materials = 1000;
                sql.UpdateUser(user);
                sql.Commit();
                msg = $"系统初始化完成！您当前账号已成为系统管理员（昵称：{user.NickName.CreateCmdInput("我的存档")}）。";
            }
            catch (Exception ex)
            {
                sql.Rollback();
                initializationConfig.Clear();
                initializationConfig.SaveConfig();
                logger.LogError(ex, "Error: {ex.Message}", ex.Message);
                msg = $"系统初始化遇到错误：{ex.Message}，请检查服务器日志！";
            }
        }
    }
}
