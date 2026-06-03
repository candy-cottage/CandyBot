using System.Data;
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
                logger.LogError(ex, "Error: {Message}", ex.Message);
                msg = $"系统初始化遇到错误：{ex.Message}，请检查服务器日志！";
            }
        }

        public void EnsureBettingAutomationSchema()
        {
            if (sql is MySQLHelper)
            {
                EnsureMySqlBettingAutomationSchema();
            }
            else if (sql is SQLiteHelper)
            {
                EnsureSqliteBettingAutomationSchema();
            }
        }

        private void EnsureMySqlBettingAutomationSchema()
        {
            if (!MySqlTableExists("betting_events") || !MySqlTableExists("betting_matches"))
            {
                return;
            }

            EnsureMySqlColumn("betting_events", "external_id",
                "ALTER TABLE `betting_events` ADD COLUMN `external_id` VARCHAR(64) NULL COMMENT '外部数据源赛事ID，手工创建为NULL' AFTER `id`");
            EnsureMySqlColumn("betting_events", "is_published",
                "ALTER TABLE `betting_events` ADD COLUMN `is_published` TINYINT(1) NOT NULL DEFAULT 1 COMMENT '是否对用户可见：0=草稿，1=已发布' AFTER `external_id`");
            EnsureMySqlColumn("betting_matches", "external_id",
                "ALTER TABLE `betting_matches` ADD COLUMN `external_id` VARCHAR(64) NULL COMMENT '外部数据源比赛ID，手工创建为NULL' AFTER `id`");
            EnsureMySqlColumn("betting_matches", "is_published",
                "ALTER TABLE `betting_matches` ADD COLUMN `is_published` TINYINT(1) NOT NULL DEFAULT 1 COMMENT '是否对用户可见：0=草稿，1=已发布' AFTER `external_id`");
            EnsureMySqlColumn("betting_matches", "pending_settlement",
                "ALTER TABLE `betting_matches` ADD COLUMN `pending_settlement` TINYINT(1) NOT NULL DEFAULT 0 COMMENT '结果已预填待管理员确认结算：0=否，1=是' AFTER `is_published`");

            EnsureMySqlUniqueIndex("betting_events", "idx_events_external_id", "CREATE UNIQUE INDEX `idx_events_external_id` ON `betting_events` (`external_id`)");
            EnsureMySqlIndex("betting_events", "idx_events_is_published", "CREATE INDEX `idx_events_is_published` ON `betting_events` (`is_published`)");
            EnsureMySqlUniqueIndex("betting_matches", "idx_matches_external_id", "CREATE UNIQUE INDEX `idx_matches_external_id` ON `betting_matches` (`external_id`)");
            EnsureMySqlIndex("betting_matches", "idx_matches_is_published", "CREATE INDEX `idx_matches_is_published` ON `betting_matches` (`is_published`)");
            EnsureMySqlIndex("betting_matches", "idx_matches_pending_settlement", "CREATE INDEX `idx_matches_pending_settlement` ON `betting_matches` (`pending_settlement`)");
        }

        private void EnsureSqliteBettingAutomationSchema()
        {
            if (!SqliteTableExists("betting_events") || !SqliteTableExists("betting_matches"))
            {
                return;
            }

            EnsureSqliteColumn("betting_events", "external_id", "ALTER TABLE `betting_events` ADD COLUMN `external_id` TEXT NULL");
            EnsureSqliteColumn("betting_events", "is_published", "ALTER TABLE `betting_events` ADD COLUMN `is_published` INTEGER NOT NULL DEFAULT 1");
            EnsureSqliteColumn("betting_matches", "external_id", "ALTER TABLE `betting_matches` ADD COLUMN `external_id` TEXT NULL");
            EnsureSqliteColumn("betting_matches", "is_published", "ALTER TABLE `betting_matches` ADD COLUMN `is_published` INTEGER NOT NULL DEFAULT 1");
            EnsureSqliteColumn("betting_matches", "pending_settlement", "ALTER TABLE `betting_matches` ADD COLUMN `pending_settlement` INTEGER NOT NULL DEFAULT 0");

            EnsureSqliteUniqueIndex("betting_events", "idx_events_external_id", "CREATE UNIQUE INDEX `idx_events_external_id` ON `betting_events` (`external_id`)");
            ExecuteSchemaStatement("CREATE INDEX IF NOT EXISTS `idx_events_is_published` ON `betting_events` (`is_published`)");
            EnsureSqliteUniqueIndex("betting_matches", "idx_matches_external_id", "CREATE UNIQUE INDEX `idx_matches_external_id` ON `betting_matches` (`external_id`)");
            ExecuteSchemaStatement("CREATE INDEX IF NOT EXISTS `idx_matches_is_published` ON `betting_matches` (`is_published`)");
            ExecuteSchemaStatement("CREATE INDEX IF NOT EXISTS `idx_matches_pending_settlement` ON `betting_matches` (`pending_settlement`)");
        }

        private bool MySqlTableExists(string table)
        {
            sql.Parameters["@table"] = table;
            DataRow? row = sql.ExecuteDataRow("SELECT COUNT(*) AS c FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @table");
            return row is not null && Convert.ToInt32(row["c"]) > 0;
        }

        private bool MySqlColumnExists(string table, string column)
        {
            sql.Parameters["@table"] = table;
            sql.Parameters["@column"] = column;
            DataRow? row = sql.ExecuteDataRow(@"SELECT COUNT(*) AS c
                FROM information_schema.columns
                WHERE table_schema = DATABASE() AND table_name = @table AND column_name = @column");
            return row is not null && Convert.ToInt32(row["c"]) > 0;
        }

        private bool MySqlIndexExists(string table, string index)
        {
            sql.Parameters["@table"] = table;
            sql.Parameters["@index"] = index;
            DataRow? row = sql.ExecuteDataRow(@"SELECT COUNT(*) AS c
                FROM information_schema.statistics
                WHERE table_schema = DATABASE() AND table_name = @table AND index_name = @index");
            return row is not null && Convert.ToInt32(row["c"]) > 0;
        }

        private bool MySqlUniqueIndexExists(string table, string index)
        {
            sql.Parameters["@table"] = table;
            sql.Parameters["@index"] = index;
            DataRow? row = sql.ExecuteDataRow(@"SELECT COUNT(*) AS c, COALESCE(MAX(non_unique), 1) AS non_unique
                FROM information_schema.statistics
                WHERE table_schema = DATABASE() AND table_name = @table AND index_name = @index");
            return row is not null && Convert.ToInt32(row["c"]) > 0 && Convert.ToInt32(row["non_unique"]) == 0;
        }

        private bool SqliteTableExists(string table)
        {
            sql.Parameters["@table"] = table;
            DataRow? row = sql.ExecuteDataRow("SELECT COUNT(*) AS c FROM sqlite_master WHERE type='table' AND name=@table");
            return row is not null && Convert.ToInt32(row["c"]) > 0;
        }

        private bool SqliteColumnExists(string table, string column)
        {
            sql.ExecuteDataSet($"PRAGMA table_info(`{table}`)");
            if (sql.Result == SQLResult.SQLError || sql.DataSet.Tables.Count == 0)
            {
                return false;
            }

            foreach (DataRow row in sql.DataSet.Tables[0].Rows)
            {
                if (string.Equals(row["name"].ToString(), column, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private void EnsureMySqlColumn(string table, string column, string statement)
        {
            if (!MySqlColumnExists(table, column))
            {
                ExecuteSchemaStatement(statement);
            }
        }

        private void EnsureMySqlIndex(string table, string index, string statement)
        {
            if (!MySqlIndexExists(table, index))
            {
                ExecuteSchemaStatement(statement);
            }
        }

        private void EnsureMySqlUniqueIndex(string table, string index, string statement)
        {
            if (MySqlUniqueIndexExists(table, index))
            {
                return;
            }
            if (MySqlIndexExists(table, index))
            {
                ExecuteSchemaStatement($"DROP INDEX `{index}` ON `{table}`");
            }
            ExecuteSchemaStatement(statement);
        }

        private void EnsureSqliteColumn(string table, string column, string statement)
        {
            if (!SqliteColumnExists(table, column))
            {
                ExecuteSchemaStatement(statement);
            }
        }

        private void EnsureSqliteUniqueIndex(string table, string index, string statement)
        {
            ExecuteSchemaStatement($"DROP INDEX IF EXISTS `{index}`");
            ExecuteSchemaStatement(statement);
        }

        private void ExecuteSchemaStatement(string statement)
        {
            sql.Execute(statement);
            if (sql.Result == SQLResult.SQLError)
            {
                throw sql.LastException ?? new InvalidOperationException("数据库结构升级失败。");
            }
        }
    }
}
