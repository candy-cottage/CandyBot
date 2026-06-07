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

        public void EnsureForzaMapSchema()
        {
            bool mapsExists = sql is MySQLHelper ? MySqlTableExists("Maps") : SqliteTableExists("Maps");
            if (mapsExists)
                return;

            // 如果 Maps 表不存在，将三张赛道表一并创建。
            // DDL 直接从 forza.sql 提取，MySQL/SQLite 共享同一脚本（SQLite 忽略 ENGINE/COLLATE 等子句）。
            ExecuteSchemaStatement(@"CREATE TABLE IF NOT EXISTS `Maps` (
    `Id` INT UNSIGNED NOT NULL AUTO_INCREMENT COMMENT '主键',
    `NameZh` VARCHAR(100) NOT NULL COMMENT '中文名',
    `NameEn` VARCHAR(150) NOT NULL COMMENT '英文名',
    `Aliases` JSON NULL COMMENT '别名（字符串数组）',
    `LengthKm` DECIMAL(8,3) NOT NULL COMMENT '赛道长度（公里）',
    `RouteType` VARCHAR(20) NOT NULL DEFAULT 'circuit' COMMENT '路线类型：circuit=圈图, sprint=冲刺图',
    `Surface` VARCHAR(20) NOT NULL DEFAULT 'asphalt' COMMENT '路面类型：asphalt/dirt/gravel/sand/mixed/snow/cross_country',
    `RaceCategory` VARCHAR(20) NOT NULL DEFAULT 'road' COMMENT '赛事大类：road/dirt/cross_country/street/drag/rally/showcase',
    `Characteristic` VARCHAR(20) NOT NULL DEFAULT 'balanced' COMMENT '赛道特性：handling/speed/balanced/offroad/drift/technical',
    `Difficulty` TINYINT UNSIGNED NOT NULL DEFAULT 1 COMMENT '难度 1-5（1=新手, 5=专家）',
    `Elevation` VARCHAR(20) NOT NULL DEFAULT 'gentle' COMMENT '海拔变化：flat/gentle/hilly/mountainous',
    `TurnCount` SMALLINT UNSIGNED NULL COMMENT '弯道数量（估算）',
    `RoadWidth` VARCHAR(20) NOT NULL DEFAULT 'normal' COMMENT '道路宽度：narrow/normal/wide',
    `AvgTimeSec` INT UNSIGNED NULL COMMENT '标准圈速/完赛时间（秒）',
    `WorldRecord` VARCHAR(100) NOT NULL DEFAULT '' COMMENT '世界纪录时间（字符串，如 01:23.456）',
    `WrVehicle` VARCHAR(200) NOT NULL DEFAULT '' COMMENT '世界纪录所用车辆（品牌+型号）',
    `WrTuning` VARCHAR(500) NOT NULL DEFAULT '' COMMENT '世界纪录车辆调校参数',
    `CarClassBest` JSON NULL COMMENT '推荐车辆等级，如 [""S1"",""S2""]',
    `CarTypeBest` JSON NULL COMMENT '推荐车辆类型，如 [""hypercar"",""rally""]',
    `Region` VARCHAR(80) NULL COMMENT '所在区域/地名',
    `SeasonBest` VARCHAR(20) NOT NULL DEFAULT 'all' COMMENT '最佳赛季：spring/summer/autumn/winter/wet/dry/all',
    `WeatherDefault` VARCHAR(20) NOT NULL DEFAULT 'clear' COMMENT '默认天气：clear/rain/fog/storm/snow/variable',
    `NightMode` TINYINT(1) NOT NULL DEFAULT 0 COMMENT '是否有夜间版本（0=否, 1=是）',
    `Dlc` VARCHAR(100) NULL COMMENT '所属DLC/扩展包名称',
    `IsReverse` TINYINT(1) NOT NULL DEFAULT 0 COMMENT '是否为反向赛道（0=正向, 1=反向）',
    `ImageUrl` VARCHAR(500) NULL COMMENT '赛道预览图URL',
    `MinimapUrl` VARCHAR(500) NULL COMMENT '小地图URL',
    `Notes` TEXT NULL COMMENT '备注',
    `CreatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '创建时间',
    `UpdatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP COMMENT '更新时间',
    PRIMARY KEY (`Id`),
    KEY `idx_maps_name_zh` (`NameZh`),
    KEY `idx_maps_name_en` (`NameEn`),
    KEY `idx_maps_route_type` (`RouteType`),
    KEY `idx_maps_surface` (`Surface`),
    KEY `idx_maps_race_category` (`RaceCategory`),
    KEY `idx_maps_characteristic` (`Characteristic`),
    KEY `idx_maps_difficulty` (`Difficulty`),
    KEY `idx_maps_region` (`Region`),
    KEY `idx_maps_dlc` (`Dlc`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='赛道表'");

            ExecuteSchemaStatement(@"CREATE TABLE IF NOT EXISTS `MapGroups` (
    `Id` INT UNSIGNED NOT NULL AUTO_INCREMENT COMMENT '主键',
    `NameZh` VARCHAR(100) NOT NULL COMMENT '中文组名',
    `NameEn` VARCHAR(150) NULL COMMENT '英文组名',
    `Description` TEXT NULL COMMENT '组描述（推荐理由等）',
    `GroupType` VARCHAR(20) NOT NULL DEFAULT 'custom' COMMENT '组类型：championship/car_test/theme/custom',
    `RecommendedCarClass` JSON NULL COMMENT '推荐车辆等级',
    `RecommendedCarStyle` VARCHAR(20) NOT NULL DEFAULT 'all' COMMENT '适配赛车风格：hypercar/supercar/sports/muscle/rally/offroad/classic/tuner/truck/buggy/track_toy/all',
    `SeasonPreferred` VARCHAR(20) NOT NULL DEFAULT 'all' COMMENT '适配赛季：spring/summer/autumn/winter/wet/dry/all',
    `WeatherPreferred` VARCHAR(20) NOT NULL DEFAULT 'all' COMMENT '适配天气：clear/rain/fog/storm/snow/variable/all',
    `Tags` JSON NULL COMMENT '标签数组',
    `Notes` TEXT NULL COMMENT '备注',
    `CreatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '创建时间',
    `UpdatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP COMMENT '更新时间',
    PRIMARY KEY (`Id`),
    KEY `idx_map_groups_group_type` (`GroupType`),
    KEY `idx_map_groups_car_style` (`RecommendedCarStyle`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='赛道组表'");

            ExecuteSchemaStatement(@"CREATE TABLE IF NOT EXISTS `MapGroupMaps` (
    `Id` INT UNSIGNED NOT NULL AUTO_INCREMENT COMMENT '关联ID',
    `GroupId` INT UNSIGNED NOT NULL COMMENT '赛道组ID',
    `MapId` INT UNSIGNED NOT NULL COMMENT '赛道ID',
    `SortOrder` TINYINT UNSIGNED NOT NULL DEFAULT 0 COMMENT '组内排序',
    PRIMARY KEY (`Id`),
    UNIQUE KEY `uk_map_group_map` (`GroupId`, `MapId`),
    KEY `idx_mgm_group_id` (`GroupId`),
    KEY `idx_mgm_map_id` (`MapId`),
    CONSTRAINT `fk_mgm_group` FOREIGN KEY (`GroupId`) REFERENCES `MapGroups` (`Id`) ON DELETE CASCADE ON UPDATE CASCADE,
    CONSTRAINT `fk_mgm_map` FOREIGN KEY (`MapId`) REFERENCES `Maps` (`Id`) ON DELETE CASCADE ON UPDATE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='赛道与赛道组关联表'");
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
