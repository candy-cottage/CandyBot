using System.Data;
using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Utilities;
using Milimoe.FunGame.Core.Api.Transmittal;
using Milimoe.FunGame.Core.Api.Utility;
using Milimoe.FunGame.Core.Entity;
using Milimoe.FunGame.Core.Library.Constant;
using Milimoe.FunGame.Core.Library.SQLScript.Common;
using ProjectRedbud.FunGame.SQLQueryExtension;
using ForzaTuneIdentity = CandyCottage.CandyBot.Utilities.ForzaTuneIdentity;

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
            // 本项目生产环境使用 MySQL；SQLite 仅为 FunGame 库兼容性保留，
            // 不做 DDL 兜底（SQLite 无法解析 AUTO_INCREMENT/KEY/ENGINE 等 MySQL 方言）。
            if (sql is not MySQLHelper)
                return;

            if (MySqlTableExists("Maps"))
                return;
            ExecuteSchemaStatement(@"CREATE TABLE IF NOT EXISTS `Maps` (
    `Id` INT UNSIGNED NOT NULL AUTO_INCREMENT COMMENT '主键',
    `NameZh` VARCHAR(100) NOT NULL COMMENT '中文名',
    `NameEn` VARCHAR(150) NOT NULL COMMENT '英文名',
    `Aliases` JSON NULL COMMENT '别名（字符串数组）',
    `LengthKm` DECIMAL(8,3) NOT NULL COMMENT '赛道长度（公里）',
    `RouteType` VARCHAR(20) NOT NULL DEFAULT 'circuit' COMMENT '路线类型：circuit=圈图, sprint=冲刺图',
    `Surface` VARCHAR(20) NULL COMMENT '路面类型：asphalt/dirt/gravel/sand/mixed/snow/cross_country',
    `RaceCategory` VARCHAR(20) NOT NULL DEFAULT 'road' COMMENT '赛事大类：road/dirt/cross_country/street/drag/rally/showcase/mountain_road',
    `Characteristic` VARCHAR(20) NULL COMMENT '赛道特性：handling/speed/balanced/offroad/drift/technical',
    `Difficulty` TINYINT UNSIGNED NULL COMMENT '难度 1-5（1=新手, 5=专家）',
    `Elevation` VARCHAR(20) NULL COMMENT '海拔变化：flat/gentle/hilly/mountainous',
    `TurnCount` SMALLINT UNSIGNED NULL COMMENT '弯道数量（估算）',
    `RoadWidth` VARCHAR(20) NULL COMMENT '道路宽度：narrow/normal/wide',
    `AvgTimeSec` INT UNSIGNED NULL COMMENT '标准圈速/完赛时间（秒）',
    `WorldRecord` VARCHAR(100) NOT NULL DEFAULT '' COMMENT '世界纪录时间（字符串，如 01:23.456）',
    `WrVehicle` VARCHAR(200) NOT NULL DEFAULT '' COMMENT '世界纪录所用车辆（品牌+型号）',
    `WrTuning` VARCHAR(500) NOT NULL DEFAULT '' COMMENT '世界纪录车辆调校参数',
    `CarClassBest` JSON NULL COMMENT '推荐车辆等级，如 [""S1"",""S2""]',
    `CarTypeBest` JSON NULL COMMENT '推荐车辆类型，如 [""hypercar"",""rally""]',
    `Region` VARCHAR(80) NULL COMMENT '所在区域/地名',
    `SeasonBest` VARCHAR(20) NULL COMMENT '最佳赛季：spring/summer/autumn/winter/wet/dry/all',
    `WeatherDefault` VARCHAR(20) NULL COMMENT '默认天气：clear/rain/fog/storm/snow/variable',
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

        /// <summary>
        /// 执行 SQL 后检查结果，失败立即抛出异常。
        /// 与 <see cref="ExecuteSchemaStatement"/> 作用相同，但用于回填数据的 DML（INSERT/DELETE）：
        /// 由于 <see cref="MySQLHelper.Execute"/> 在外层事务存在时会静默吞掉异常，
        /// 任何回填语句都必须通过本方法执行，确保失败可见。
        /// 调用前须已通过 sql.Parameters 设置好所有参数；方法执行后参数已由 helper 清空。
        /// </summary>
        private void ExecuteOrThrow(string statement)
        {
            sql.Execute(statement);
            if (sql.Result == SQLResult.SQLError)
            {
                throw sql.LastException ?? new InvalidOperationException($"迁移语句失败: {statement}");
            }
        }

        // VehicleTunes 建表 DDL（与 scripts/forza.sql 保持完全一致）
        private const string VehicleTunesDdl = @"CREATE TABLE IF NOT EXISTS `VehicleTunes` (
  `Id` INT UNSIGNED NOT NULL AUTO_INCREMENT COMMENT '主键',
  `VehicleId` INT UNSIGNED NOT NULL COMMENT '关联车型ID',
  `Class` VARCHAR(20) NOT NULL COMMENT '等级',
  `MatchType` VARCHAR(20) NOT NULL COMMENT '比赛类型',
  `Drivetrain` VARCHAR(20) NULL COMMENT '传动方式: 四驱, 后驱, 前驱',
  `PerformancePreference` VARCHAR(20) NULL COMMENT '性能偏好: 均衡, 操控, 动力, 高速, 加速等',
  `TireCompound` VARCHAR(50) NULL COMMENT '胎面: 半热熔, 全热熔, 拉力胎, 直线胎, 越野胎, 漂移胎等',
  `SurfaceCondition` VARCHAR(20) NULL COMMENT '适用路面: 干, 湿地, 通用',
  `Strength` VARCHAR(20) NULL COMMENT '强度评级',
  `Tuner` VARCHAR(255) NULL COMMENT '调校师XboxID',
  `TuneRaw` VARCHAR(255) NULL COMMENT '调校码/关键词',
  `Remarks` VARCHAR(255) NULL COMMENT '备注：手感、特殊开法等',
  `LapTime` VARCHAR(255) NULL COMMENT '圈速测试结果，如""白川54.5""',
  `ExtData` TEXT NULL COMMENT '扩展字段，存储额外属性，如侧向G值、极速等',
  `SourceFileId` VARCHAR(128) NOT NULL COMMENT '来源文件ID',
  `SourceSheet` VARCHAR(128) NULL COMMENT '来源工作表名',
  `SourceRow` INT NULL COMMENT '来源行号',
  `SourceDisplayName` VARCHAR(255) NULL COMMENT '来源展示名（用于调试）',
  `SourceIdentity` VARCHAR(512) NOT NULL COLLATE utf8mb4_bin COMMENT '来源唯一标识（区分大小写，用于幂等同步）',
  `SourceIdentitySeq` INT UNSIGNED NOT NULL DEFAULT 0 COMMENT '同标识下的序号（处理同一行多条调校）',
  `SourceFingerprint` CHAR(64) NULL COMMENT '行内容指纹（SHA-256），用于检测变更',
  `IsManual` TINYINT(1) NOT NULL DEFAULT 0 COMMENT '是否手动维护（1=手动，0=自动同步）',
  `TuneStatus` VARCHAR(10) NOT NULL DEFAULT '正常' COMMENT '调校状态: 正常, 存疑, 弃用',
  `LastUpdatedUserId` BIGINT NOT NULL DEFAULT 0 COMMENT '最后更新人用户ID',
  `LastUpdatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '最后更新时间',
  `CreatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '创建时间',
  `UpdatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP COMMENT '更新时间',
  PRIMARY KEY (`Id`),
  KEY `idx_vehicle` (`VehicleId`),
  KEY `idx_class_match` (`Class`,`MatchType`),
  KEY `idx_source` (`SourceFileId`),
  KEY `idx_status` (`TuneStatus`),
  UNIQUE KEY `uq_source_identity` (`SourceFileId`,`SourceIdentity`,`SourceIdentitySeq`),
  CONSTRAINT `fk_tune_vehicle` FOREIGN KEY (`VehicleId`) REFERENCES `Vehicles`(`Id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='调校表（每辆车可有多条）'";

        // VehicleTuneCache 建表 DDL（与 scripts/forza.sql 保持完全一致）
        private const string VehicleTuneCacheDdl = @"CREATE TABLE IF NOT EXISTS `VehicleTuneCache` (
  `Id` INT UNSIGNED NOT NULL AUTO_INCREMENT COMMENT '主键',
  `TuneId` INT UNSIGNED NOT NULL COMMENT '对应 VehicleTunes.Id',
  `VehicleId` INT UNSIGNED NOT NULL COMMENT '对应 Vehicles.Id',
  `Class` VARCHAR(20) NOT NULL COMMENT '等级',
  `MatchType` VARCHAR(20) NOT NULL COMMENT '比赛类型',
  `Drivetrain` VARCHAR(20) NULL COMMENT '传动方式',
  `PerformancePreference` VARCHAR(20) NULL COMMENT '性能偏好',
  `TireCompound` VARCHAR(50) NULL COMMENT '胎面',
  `SurfaceCondition` VARCHAR(20) NULL COMMENT '适用路面',
  `Strength` VARCHAR(20) NULL COMMENT '强度评级',
  `Tuner` VARCHAR(255) NULL COMMENT '调校师XboxID',
  `TuneRaw` VARCHAR(255) NULL COMMENT '调校码/关键词',
  `Remarks` VARCHAR(255) NULL COMMENT '备注',
  `LapTime` VARCHAR(255) NULL COMMENT '圈速',
  `ExtData` TEXT NULL COMMENT '扩展字段',
  `SourceFileId` VARCHAR(128) NOT NULL COMMENT '来源文件ID',
  `SourceSheet` VARCHAR(128) NULL COMMENT '来源工作表名',
  `SourceRow` INT NULL COMMENT '来源行号',
  `SourceDisplayName` VARCHAR(255) NULL COMMENT '来源展示名',
  `SourceIdentity` VARCHAR(512) NOT NULL COLLATE utf8mb4_bin COMMENT '来源唯一标识',
  `SourceFingerprint` CHAR(64) NULL COMMENT '行内容指纹',
  `CapturedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '缓存捕获时间',
  PRIMARY KEY (`Id`),
  UNIQUE KEY `uq_cache_tune` (`TuneId`),
  KEY `idx_cache_source` (`SourceFileId`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='调校缓存表（展示用宽表）'";

        // 新版 Vehicles 车型主表 DDL（与 scripts/forza.sql 保持完全一致）
        private const string VehiclesDdl = @"CREATE TABLE IF NOT EXISTS `Vehicles` (
  `Id` INT UNSIGNED NOT NULL AUTO_INCREMENT COMMENT '主键',
  `Manufacturer` VARCHAR(100) NOT NULL COMMENT '制造商',
  `Model` VARCHAR(100) NOT NULL COMMENT '型号',
  `Year` VARCHAR(20) NULL COMMENT '年份',
  `ModelKey` VARCHAR(255) NOT NULL COLLATE utf8mb4_bin COMMENT '匹配键（制造商+型号规范化，区分大小写）',
  `IsManual` TINYINT(1) NOT NULL DEFAULT 0 COMMENT '是否手动维护（1=手动，0=自动同步）',
  `LastUpdatedUserId` BIGINT NOT NULL DEFAULT 0 COMMENT '最后更新人用户ID',
  `LastUpdatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '最后更新时间',
  `CreatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '创建时间',
  `UpdatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP COMMENT '更新时间',
  PRIMARY KEY (`Id`),
  UNIQUE KEY `uq_modelkey` (`ModelKey`),
  KEY `idx_manufacturer_model` (`Manufacturer`,`Model`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='车型主表'";

        /// <summary>
        /// Forza v1→v2 模式迁移：将旧版单合并 <c>Vehicles</c> 表拆分为
        /// <c>Vehicles</c>（车型主表）、<c>VehicleTunes</c>（调校表）、<c>VehicleTuneCache</c>（展示宽表）。
        ///
        /// <para>算法要点：</para>
        /// <list type="bullet">
        ///   <item>仅对 MySQL 执行；SQLite 直接返回。</item>
        ///   <item>用 <c>PluginConfig("forza","schema")["version"]</c> 标记是否已完成；值为 "2" 则幂等返回。</item>
        ///   <item>旧表特征列：<c>Tune</c>（无 <c>ModelKey</c>）。</item>
        ///   <item>DDL（RENAME/CREATE）不可被事务回滚；回填 DML 通过 <see cref="ExecuteOrThrow"/> 保证非静默失败。</item>
        ///   <item>车型按 <see cref="ForzaTuneIdentity.BuildModelKey"/> 去重；Id 通过 SELECT 回读而非信任 LAST_INSERT_ID。</item>
        ///   <item>
        ///     replay 守护：<c>Vehicles_legacy_v1</c> 存在即意味着迁移 DDL 已执行，
        ///     无论 <c>Vehicles</c> 当前是否还有 <c>Tune</c> 列，都需检查 VehicleTunes 回填计数，
        ///     残缺时先 DELETE migration 行再完整重灌（可重放）。
        ///   </item>
        ///   <item>置版本标记前必须通过 count 校验，失败即抛出，绝不静默标记。</item>
        /// </list>
        /// </summary>
        public void EnsureForzaTuneSchema()
        {
            // 前置守卫1：SQLite 不迁移
            if (sql is not MySQLHelper)
                return;

            // 前置守卫2：读取版本标记，已完成则幂等返回
            PluginConfig schemaVerCfg = new("forza", "schema");
            schemaVerCfg.LoadConfig();
            if ((schemaVerCfg.Get<string>("version") ?? "") == "2")
                return;

            // 前置守卫3：Vehicles 表不存在说明尚未系统初始化；新建表由 forza.sql 负责，此处直接返回
            if (!MySqlTableExists("Vehicles"))
                return;

            bool legacyShape = MySqlColumnExists("Vehicles", "Tune");
            bool legacyBackupExists = MySqlTableExists("Vehicles_legacy_v1");

            if (legacyShape)
            {
                // ── 路径A：旧合并表仍在 Vehicles 上，执行完整 DDL 迁移步骤 ──

                // Step A：建子表（CREATE IF NOT EXISTS，DDL 不可回滚，但幂等安全）
                ExecuteSchemaStatement(VehicleTunesDdl);
                ExecuteSchemaStatement(VehicleTuneCacheDdl);

                // Step B：RENAME 旧合并表（DDL，隐式提交，不可回滚）
                if (!legacyBackupExists)
                    ExecuteSchemaStatement("RENAME TABLE `Vehicles` TO `Vehicles_legacy_v1`");

                // Step C：建新 Vehicles 车型主表
                if (!MySqlTableExists("Vehicles"))
                    ExecuteSchemaStatement(VehiclesDdl);
            }
            else if (!legacyBackupExists)
            {
                // ── 路径B：既无旧合并表，backup 也不存在 ──
                // 若无 ModelKey 则为半建状态，补建子表
                if (!MySqlColumnExists("Vehicles", "ModelKey"))
                {
                    ExecuteSchemaStatement(VehicleTunesDdl);
                    ExecuteSchemaStatement(VehicleTuneCacheDdl);
                }
                // 新装 or 已完整新建：无旧数据可回填，直接置标记
                schemaVerCfg.Add("version", "2");
                schemaVerCfg.SaveConfig();
                logger.LogInformation("Forza schema 升级至 v2（新装或无旧数据，无需回填）");
                return;
            }
            // else：legacyShape==false && legacyBackupExists：DDL 已执行过，进入回填校验路径

            // ── 共用回填路径：legacy_v1 存在时（路径A/路径C），检查并按需重灌 VehicleTunes ──

            // Step D：读取旧合并表所有行
            DataSet legacyDs = sql.ExecuteDataSet("SELECT * FROM `Vehicles_legacy_v1`");
            if (sql.Result == SQLResult.SQLError)
                throw sql.LastException ?? new InvalidOperationException("读取 Vehicles_legacy_v1 失败");
            DataTable legacyRows = legacyDs.Tables.Count > 0 ? legacyDs.Tables[0] : new DataTable();
            int legacyCount = legacyRows.Rows.Count;

            // Step E：检查回填计数；残缺时清空重灌（可重放）
            DataRow? tuneCountRow = sql.ExecuteDataRow(
                "SELECT COUNT(*) AS c FROM `VehicleTunes` WHERE SourceFileId='migration'");
            if (sql.Result == SQLResult.SQLError)
                throw sql.LastException ?? new InvalidOperationException("读取 VehicleTunes migration 行数失败");
            int tuneMig = tuneCountRow != null ? Convert.ToInt32(tuneCountRow["c"]) : 0;

            if (tuneMig != legacyCount)
            {
                // 清除半成品 migration 行（ExecuteOrThrow 确保删除不静默失败）
                ExecuteOrThrow("DELETE FROM `VehicleTunes` WHERE SourceFileId='migration'");

                // Step E1：车型去重回填 Vehicles，用 ON DUPLICATE KEY UPDATE；Id 通过 SELECT 回读
                Dictionary<string, long> modelIdByKey = [];

                foreach (DataRow row in legacyRows.Rows)
                {
                    string manufacturer = row["Manufacturer"]?.ToString() ?? "";
                    string model = row["Model"]?.ToString() ?? "";
                    string year = row["Year"]?.ToString() ?? "";
                    string modelKey = ForzaTuneIdentity.BuildModelKey(manufacturer, model, year);

                    if (!modelIdByKey.ContainsKey(modelKey))
                    {
                        // INSERT ... ON DUPLICATE KEY UPDATE Id=LAST_INSERT_ID(Id)
                        // 确保：若 uq_modelkey 冲突，LastInsertId 依然有效（MySQL 扩展语义）
                        sql.Parameters["@manufacturer"] = manufacturer;
                        sql.Parameters["@model"] = model;
                        sql.Parameters["@year"] = year.Length > 0 ? (object)year : DBNull.Value;
                        sql.Parameters["@modelKey"] = modelKey;
                        ExecuteOrThrow(@"INSERT INTO `Vehicles` (Manufacturer, Model, Year, ModelKey, IsManual)
                            VALUES (@manufacturer, @model, @year, @modelKey, 1)
                            ON DUPLICATE KEY UPDATE Id = LAST_INSERT_ID(Id)");

                        // 回读 Id（比信任 LastInsertId 更稳；uq_modelkey 唯一）
                        sql.Parameters["@modelKey"] = modelKey;
                        DataRow? vehicleRow = sql.ExecuteDataRow(
                            "SELECT Id FROM `Vehicles` WHERE ModelKey = @modelKey");
                        if (sql.Result == SQLResult.SQLError || vehicleRow == null)
                            throw sql.LastException ?? new InvalidOperationException($"无法读取车型 Id，ModelKey={modelKey}");
                        modelIdByKey[modelKey] = Convert.ToInt64(vehicleRow["Id"]);
                    }
                }

                // Step E2：每条旧行 → 1 条调校记录，SourceFileId='migration'，SourceIdentity=''（哨兵）
                int seq = 0;
                foreach (DataRow row in legacyRows.Rows)
                {
                    string manufacturer = row["Manufacturer"]?.ToString() ?? "";
                    string model = row["Model"]?.ToString() ?? "";
                    string year = row["Year"]?.ToString() ?? "";
                    string modelKey = ForzaTuneIdentity.BuildModelKey(manufacturer, model, year);
                    long vehicleId = modelIdByKey[modelKey];

                    // 旧列映射：Attribute→PerformancePreference；Tune→TuneRaw
                    sql.Parameters["@vehicleId"] = vehicleId;
                    sql.Parameters["@class"] = row["Class"]?.ToString() ?? "";
                    sql.Parameters["@matchType"] = row["MatchType"]?.ToString() ?? "";
                    sql.Parameters["@drivetrain"] = NullIfEmpty(row["Drivetrain"]?.ToString());
                    sql.Parameters["@perfPref"] = NullIfEmpty(row["Attribute"]?.ToString());
                    sql.Parameters["@tireCompound"] = NullIfEmpty(row["TireCompound"]?.ToString());
                    sql.Parameters["@surfaceCond"] = NullIfEmpty(row["SurfaceCondition"]?.ToString());
                    sql.Parameters["@strength"] = NullIfEmpty(row["Strength"]?.ToString());
                    sql.Parameters["@tuneRaw"] = NullIfEmpty(row["Tune"]?.ToString());
                    sql.Parameters["@remarks"] = NullIfEmpty(row["Remarks"]?.ToString());
                    sql.Parameters["@lapTime"] = NullIfEmpty(row["LapTime"]?.ToString());
                    sql.Parameters["@extData"] = NullIfEmpty(row["ExtData"]?.ToString());
                    sql.Parameters["@seq"] = seq;

                    ExecuteOrThrow(@"INSERT INTO `VehicleTunes`
                        (VehicleId, Class, MatchType, Drivetrain, PerformancePreference,
                         TireCompound, SurfaceCondition, Strength, TuneRaw, Remarks, LapTime, ExtData,
                         SourceFileId, SourceIdentity, SourceIdentitySeq, IsManual, TuneStatus, Tuner, SourceFingerprint)
                        VALUES
                        (@vehicleId, @class, @matchType, @drivetrain, @perfPref,
                         @tireCompound, @surfaceCond, @strength, @tuneRaw, @remarks, @lapTime, @extData,
                         'migration', '', @seq, 1, '正常', NULL, NULL)");
                    seq++;
                }
            }

            // Step F：count 校验，通过后才置版本标记（未通过即抛出，绝不静默标记）
            DataRow? finalCountRow = sql.ExecuteDataRow(
                "SELECT COUNT(*) AS c FROM `VehicleTunes` WHERE SourceFileId='migration'");
            if (sql.Result == SQLResult.SQLError)
                throw sql.LastException ?? new InvalidOperationException("迁移后 VehicleTunes count 校验查询失败");
            int finalCount = finalCountRow != null ? Convert.ToInt32(finalCountRow["c"]) : 0;
            if (finalCount != legacyCount)
                throw new InvalidOperationException(
                    $"迁移 count 校验失败：VehicleTunes migration 行数={finalCount}，legacy 行数={legacyCount}，拒绝置版本标记。");

            // Step G：置版本标记（仅在 count 校验通过后才到此处）
            schemaVerCfg.Add("version", "2");
            schemaVerCfg.SaveConfig();
            logger.LogInformation("Forza schema 已升级至 v2（Vehicles/VehicleTunes/VehicleTuneCache 三表结构）");
        }

        /// <summary>
        /// 将空字符串或纯空白转为 <see cref="DBNull.Value"/>，非空则返回原字符串。
        /// 用于旧表迁移回填时区分"有内容"与"无内容"。
        /// </summary>
        private static object NullIfEmpty(string? value) =>
            string.IsNullOrWhiteSpace(value) ? DBNull.Value : (object)value;
    }
}
