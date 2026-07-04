using CandyBot.IntegrationTests.Infrastructure;
using CandyCottage.CandyBot.Services.SystemServices;
using CandyCottage.CandyBot.Utilities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Milimoe.FunGame.Core.Api.Utility;
using Milimoe.FunGame.Core.Library.Constant;
using MySql.Data.MySqlClient;
using Xunit;

namespace CandyBot.IntegrationTests.Forza;

/// <summary>
/// 针对 <see cref="SQLTableInitializeService.EnsureForzaTuneSchema"/> 的集成测试（Task 5）。
///
/// <para>测试策略：每个用例使用 <see cref="MySqlFixture.CreateFreshDatabaseAsync"/> 获取一个空库，
/// 手工建老版 <c>Vehicles</c> 合并表（含 <c>Tune</c> 列），灌入种子行，再调迁移方法验证结果。</para>
///
/// <para>六个用例：</para>
/// <list type="number">
///   <item>正常迁移：验证 车型去重、调校行数、哨兵来源标记、legacy 表保留、schema 版本号已置。</item>
///   <item>幂等性：再次调用不报错、行数不变。</item>
///   <item>半成品恢复：强制清除版本标记、删一条 migration 调校行，再次调用须完整重放到正确行数。</item>
///   <item>旧表 Id 不连续（PR #22 事故回归）：外键必须绑定新 Vehicles，迁移不得失败。</item>
///   <item>外键误绑修复：历史坏状态（fk 指向 legacy）且表内仅迁移行时，自动修复并完成回填。</item>
///   <item>外键误绑拒绝：坏状态且存在非迁移调校时，拒绝自动修复并抛出异常，数据保持原样。</item>
/// </list>
/// </summary>
[Collection(MySqlCollection.Name)]
[Trait("Category", "MySql")]
public class ForzaMigrationTests(MySqlFixture fixture)
{
    private readonly MySqlFixture _fixture = fixture;

    // ──────────────────────────────────────────────────────────────────────────
    // DDL：旧版单合并表（含 Tune 列，无 ModelKey 列）
    // ──────────────────────────────────────────────────────────────────────────
    private const string OldVehiclesDdl = @"
CREATE TABLE `Vehicles` (
  `Id` INT UNSIGNED NOT NULL AUTO_INCREMENT,
  `Manufacturer` VARCHAR(100) NOT NULL,
  `Model` VARCHAR(100) NOT NULL,
  `Year` VARCHAR(20) NULL,
  `Class` VARCHAR(20) NOT NULL,
  `MatchType` VARCHAR(20) NOT NULL,
  `Drivetrain` VARCHAR(20) NULL,
  `Attribute` VARCHAR(20) NULL COMMENT '旧列，对应新表 PerformancePreference',
  `TireCompound` VARCHAR(50) NULL,
  `SurfaceCondition` VARCHAR(20) NULL,
  `Strength` VARCHAR(20) NULL,
  `Tune` VARCHAR(255) NULL COMMENT '旧列，对应新表 TuneRaw',
  `Remarks` VARCHAR(255) NULL,
  `LapTime` VARCHAR(255) NULL,
  `ExtData` TEXT NULL,
  PRIMARY KEY (`Id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci";

    // ──────────────────────────────────────────────────────────────────────────
    // DDL 复本：与生产 forza.sql 一致，用于复现历史版本（错误的 DDL 顺序）产出的坏状态。
    // 注意 VehicleTunes 外键写的是 REFERENCES `Vehicles`——建表时 Vehicles 还是旧合并表，
    // 随后 RENAME 时 MySQL 会让外键跟随改名，从而复现 fk 误指向 Vehicles_legacy_v1。
    // ──────────────────────────────────────────────────────────────────────────
    private const string VehicleTunesDdlCopy = @"CREATE TABLE `VehicleTunes` (
  `Id` INT UNSIGNED NOT NULL AUTO_INCREMENT,
  `VehicleId` INT UNSIGNED NOT NULL,
  `Class` VARCHAR(20) NOT NULL,
  `MatchType` VARCHAR(20) NOT NULL,
  `Drivetrain` VARCHAR(20) NULL,
  `PerformancePreference` VARCHAR(20) NULL,
  `TireCompound` VARCHAR(50) NULL,
  `SurfaceCondition` VARCHAR(20) NULL,
  `Strength` VARCHAR(20) NULL,
  `Tuner` VARCHAR(255) NULL,
  `TuneRaw` VARCHAR(255) NULL,
  `Remarks` VARCHAR(255) NULL,
  `LapTime` VARCHAR(255) NULL,
  `ExtData` TEXT NULL,
  `SourceFileId` VARCHAR(128) NOT NULL,
  `SourceSheet` VARCHAR(128) NULL,
  `SourceRow` INT NULL,
  `SourceDisplayName` VARCHAR(255) NULL,
  `SourceIdentity` VARCHAR(512) NOT NULL COLLATE utf8mb4_bin,
  `SourceIdentitySeq` INT UNSIGNED NOT NULL DEFAULT 0,
  `SourceFingerprint` CHAR(64) NULL,
  `IsManual` TINYINT(1) NOT NULL DEFAULT 0,
  `TuneStatus` VARCHAR(10) NOT NULL DEFAULT '正常',
  `LastUpdatedUserId` BIGINT NOT NULL DEFAULT 0,
  `LastUpdatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `CreatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `UpdatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`Id`),
  KEY `idx_vehicle` (`VehicleId`),
  UNIQUE KEY `uq_source_identity` (`SourceFileId`,`SourceIdentity`,`SourceIdentitySeq`),
  CONSTRAINT `fk_tune_vehicle` FOREIGN KEY (`VehicleId`) REFERENCES `Vehicles`(`Id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci";

    private const string NewVehiclesDdlCopy = @"CREATE TABLE `Vehicles` (
  `Id` INT UNSIGNED NOT NULL AUTO_INCREMENT,
  `Manufacturer` VARCHAR(100) NOT NULL,
  `Model` VARCHAR(100) NOT NULL,
  `Year` VARCHAR(20) NULL,
  `ModelKey` VARCHAR(255) NOT NULL COLLATE utf8mb4_bin,
  `IsManual` TINYINT(1) NOT NULL DEFAULT 0,
  `LastUpdatedUserId` BIGINT NOT NULL DEFAULT 0,
  `LastUpdatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `CreatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `UpdatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`Id`),
  UNIQUE KEY `uq_modelkey` (`ModelKey`),
  KEY `idx_manufacturer_model` (`Manufacturer`,`Model`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci";

    // 种子行：显式指定不连续 Id（模拟线上因删除/重同步产生的 Id 空洞）。
    // 新 Vehicles 车型主表回填后 Id 从 1 开始，与 501/502/503 无交集——
    // 若外键误绑 legacy 表，回填第一条调校即触发外键违规（Milimoe 线上事故场景）。
    private const string SeedGappedIdRows = @"INSERT INTO `Vehicles`
        (Id, Manufacturer, Model, Year, Class, MatchType, Drivetrain, Attribute, TireCompound, SurfaceCondition, Strength, Tune, Remarks, LapTime, ExtData)
        VALUES
        (501, 'Honda', 'Civic', '2022', 'A', 'Road', 'FWD', '操控', '半热熔', '干', 'S', 'tune_abc', '好开', '1:02.3', NULL),
        (502, 'Honda', 'Civic', '2022', 'A', 'Road', 'AWD', '均衡', '全热熔', '湿地', 'A', 'tune_xyz', '稳', '1:03.1', NULL),
        (503, 'Toyota', 'Supra', '1998', 'S1', 'Road', 'RWD', '动力', '半热熔', '干', 'S', 'tune_qrs', '漂逸', '0:58.0', NULL)";

    // ──────────────────────────────────────────────────────────────────────────
    // 辅助：在指定连接上直接执行 SQL（绕开 MySQLHelper 的 LastException 静默机制）
    // ──────────────────────────────────────────────────────────────────────────
    private static async Task ExecuteRawAsync(string connectionString, string sql)
    {
        await using MySqlConnection conn = new(connectionString);
        await conn.OpenAsync();
        await using MySqlCommand cmd = new(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using MySqlConnection conn = new(connectionString);
        await conn.OpenAsync();
        await using MySqlCommand cmd = new(sql, conn);
        object? r = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(r ?? 0L);
    }

    /// <summary>查询 fk_tune_vehicle 外键当前引用的父表名（不存在则返回空串）。</summary>
    private static async Task<string> FkReferencedTableAsync(string connectionString)
    {
        await using MySqlConnection conn = new(connectionString);
        await conn.OpenAsync();
        await using MySqlCommand cmd = new(@"SELECT REFERENCED_TABLE_NAME
            FROM information_schema.KEY_COLUMN_USAGE
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'VehicleTunes'
              AND CONSTRAINT_NAME = 'fk_tune_vehicle' AND REFERENCED_TABLE_NAME IS NOT NULL
            LIMIT 1", conn);
        object? r = await cmd.ExecuteScalarAsync();
        return r?.ToString() ?? "";
    }

    /// <summary>
    /// 复现历史版本（错误的 DDL 顺序）产出的坏状态：
    /// 先建 VehicleTunes（外键绑到当时的旧 Vehicles），再 RENAME——外键跟随改名误指向
    /// Vehicles_legacy_v1——最后建新 Vehicles 车型主表。版本标记未置、无回填数据。
    /// </summary>
    private static async Task SetupMisboundFkStateAsync(string connectionString)
    {
        await ExecuteRawAsync(connectionString, OldVehiclesDdl);
        await ExecuteRawAsync(connectionString, SeedGappedIdRows);
        await ExecuteRawAsync(connectionString, VehicleTunesDdlCopy);
        await ExecuteRawAsync(connectionString, "RENAME TABLE `Vehicles` TO `Vehicles_legacy_v1`");
        await ExecuteRawAsync(connectionString, NewVehiclesDdlCopy);
    }

    private static string BuildCs(MySqlFixture f, string db)
    {
        return new MySqlConnectionStringBuilder
        {
            Server = f.Host,
            Port = f.Port,
            UserID = f.Username,
            Password = f.ConnectionPassword,
            Database = db,
            CharacterSet = "utf8mb4",
        }.ToString();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 测试 1：正常迁移
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnsureForzaTuneSchema_旧合并表_迁移后车型去重_调校行数正确_标记置2()
    {
        (string db, MySQLHelper sql) = await _fixture.CreateFreshDatabaseAsync();
        string originalRootPath = PluginConfig.RootPath;
        string tempCfgDir = Path.Combine(Path.GetTempPath(), "forza-mig-test-" + Guid.NewGuid().ToString("N"));
        string cs = BuildCs(_fixture, db);
        try
        {
            PluginConfig.RootPath = tempCfgDir;

            // 手工建旧版 Vehicles（含 Tune 列）
            await ExecuteRawAsync(cs, OldVehiclesDdl);

            // 种 3 行：前 2 行共享同一车型（Honda Civic 2022），第 3 行为独立车型
            await ExecuteRawAsync(cs, @"INSERT INTO `Vehicles`
                (Manufacturer, Model, Year, Class, MatchType, Drivetrain, Attribute, TireCompound, SurfaceCondition, Strength, Tune, Remarks, LapTime, ExtData)
                VALUES
                ('Honda', 'Civic', '2022', 'A', 'Road', 'FWD', '操控', '半热熔', '干', 'S', 'tune_abc', '好开', '1:02.3', NULL),
                ('Honda', 'Civic', '2022', 'A', 'Road', 'AWD', '均衡', '全热熔', '湿地', 'A', 'tune_xyz', '稳', '1:03.1', NULL),
                ('Toyota', 'Supra', '1998', 'S1', 'Road', 'RWD', '动力', '半热熔', '干', 'S', 'tune_qrs', '漂逸', '0:58.0', NULL)");

            SQLTableInitializeService svc = new(NullLogger<SQLTableInitializeService>.Instance, sql);
            svc.EnsureForzaTuneSchema();

            // 断言1：车型表去重 → 2 行（Honda Civic 2022 合并为 1 行，Toyota Supra 1 行）
            long vehicleCount = await ScalarAsync(cs, "SELECT COUNT(*) FROM `Vehicles`");
            vehicleCount.Should().Be(2, "3 条旧记录中 Honda Civic 2022 应合并为 1 条车型");

            // 断言2：调校表 → 3 行，全部标记来源为 migration
            long tuneCount = await ScalarAsync(cs, "SELECT COUNT(*) FROM `VehicleTunes`");
            tuneCount.Should().Be(3, "3 条旧记录应各产生 1 条调校记录");

            long migrationTunes = await ScalarAsync(cs, "SELECT COUNT(*) FROM `VehicleTunes` WHERE SourceFileId='migration'");
            migrationTunes.Should().Be(3, "所有回填调校的 SourceFileId 应为 'migration'");

            long manualTunes = await ScalarAsync(cs, "SELECT COUNT(*) FROM `VehicleTunes` WHERE IsManual=1");
            manualTunes.Should().Be(3, "所有回填调校应标记 IsManual=1");

            long emptyIdentityTunes = await ScalarAsync(cs, "SELECT COUNT(*) FROM `VehicleTunes` WHERE SourceIdentity=''");
            emptyIdentityTunes.Should().Be(3, "所有回填调校的 SourceIdentity 应为空串（哨兵）");

            // 断言3：旧表 backup 存在
            long legacyExists = await ScalarAsync(cs,
                "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='Vehicles_legacy_v1'");
            legacyExists.Should().Be(1, "旧合并表应已重命名为 Vehicles_legacy_v1");

            // 断言4：schema 版本标记 = "2"
            PluginConfig cfg = new("forza", "schema");
            cfg.LoadConfig();
            string version = cfg.Get<string>("version") ?? "";
            version.Should().Be("2", "迁移完成后 forza schema 版本标记应置为 '2'");

            // 断言5：新 Vehicles 表有 ModelKey 列 & uq_modelkey 索引（与 forza.sql 新建一致）
            long modelKeyColCount = await ScalarAsync(cs,
                "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='Vehicles' AND column_name='ModelKey'");
            modelKeyColCount.Should().Be(1, "迁移后 Vehicles 应有 ModelKey 列");

            long uqModelKeyCount = await ScalarAsync(cs,
                "SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema=DATABASE() AND table_name='Vehicles' AND index_name='uq_modelkey' AND non_unique=0");
            uqModelKeyCount.Should().BeGreaterThan(0, "迁移后 Vehicles 应有 uq_modelkey 唯一索引");

            // 断言6：VehicleTunes 有 fk_tune_vehicle 外键，且引用的是新 Vehicles 车型主表——
            // MySQL 外键跟随 RENAME，若 DDL 顺序错误会误指向 Vehicles_legacy_v1（PR #22 事故）
            long fkCount = await ScalarAsync(cs,
                "SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=DATABASE() AND CONSTRAINT_NAME='fk_tune_vehicle'");
            fkCount.Should().Be(1, "迁移后 VehicleTunes 应有 fk_tune_vehicle 外键");
            (await FkReferencedTableAsync(cs)).Should().BeEquivalentTo("Vehicles",
                "fk_tune_vehicle 必须引用新 Vehicles 车型主表，而非 Vehicles_legacy_v1");

            // 断言7：VehicleTunes.SourceIdentity 排序规则为 utf8mb4_bin
            long binCollationCount = await ScalarAsync(cs,
                "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='VehicleTunes' AND column_name='SourceIdentity' AND collation_name='utf8mb4_bin'");
            binCollationCount.Should().Be(1, "VehicleTunes.SourceIdentity 应为 utf8mb4_bin 排序规则");
        }
        finally
        {
            PluginConfig.RootPath = originalRootPath;
            sql.Dispose();
            try { if (Directory.Exists(tempCfgDir)) Directory.Delete(tempCfgDir, recursive: true); } catch { }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 测试 2：幂等性——再调一次不报错、行数不变
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnsureForzaTuneSchema_调用两次_幂等无异常_行数不变()
    {
        (string db, MySQLHelper sql) = await _fixture.CreateFreshDatabaseAsync();
        string originalRootPath = PluginConfig.RootPath;
        string tempCfgDir = Path.Combine(Path.GetTempPath(), "forza-mig-idempotent-" + Guid.NewGuid().ToString("N"));
        string cs = BuildCs(_fixture, db);
        try
        {
            PluginConfig.RootPath = tempCfgDir;

            await ExecuteRawAsync(cs, OldVehiclesDdl);
            await ExecuteRawAsync(cs, @"INSERT INTO `Vehicles`
                (Manufacturer, Model, Year, Class, MatchType, Drivetrain, Attribute, TireCompound, SurfaceCondition, Strength, Tune, Remarks, LapTime, ExtData)
                VALUES
                ('Honda', 'Civic', '2022', 'A', 'Road', 'FWD', '操控', '半热熔', '干', 'S', 'tune_abc', '好开', '1:02.3', NULL),
                ('Honda', 'Civic', '2022', 'A', 'Road', 'AWD', '均衡', '全热熔', '湿地', 'A', 'tune_xyz', '稳', '1:03.1', NULL),
                ('Toyota', 'Supra', '1998', 'S1', 'Road', 'RWD', '动力', '半热熔', '干', 'S', 'tune_qrs', '漂逸', '0:58.0', NULL)");

            SQLTableInitializeService svc = new(NullLogger<SQLTableInitializeService>.Instance, sql);

            // 第一次迁移
            svc.EnsureForzaTuneSchema();

            // 记下行数
            long vehiclesBefore = await ScalarAsync(cs, "SELECT COUNT(*) FROM `Vehicles`");
            long tunesBefore = await ScalarAsync(cs, "SELECT COUNT(*) FROM `VehicleTunes`");

            // 第二次迁移（需幂等：应因版本标记 == "2" 直接返回）
            Action secondCall = () => svc.EnsureForzaTuneSchema();
            secondCall.Should().NotThrow("第二次调用 EnsureForzaTuneSchema 应幂等无异常");

            long vehiclesAfter = await ScalarAsync(cs, "SELECT COUNT(*) FROM `Vehicles`");
            long tunesAfter = await ScalarAsync(cs, "SELECT COUNT(*) FROM `VehicleTunes`");

            vehiclesAfter.Should().Be(vehiclesBefore, "幂等调用后 Vehicles 行数不应改变");
            tunesAfter.Should().Be(tunesBefore, "幂等调用后 VehicleTunes 行数不应改变");
        }
        finally
        {
            PluginConfig.RootPath = originalRootPath;
            sql.Dispose();
            try { if (Directory.Exists(tempCfgDir)) Directory.Delete(tempCfgDir, recursive: true); } catch { }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 测试 3：半成品恢复——删掉 1 条 migration 调校 + 清除版本标记，再次调用须完整重放
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnsureForzaTuneSchema_半成品恢复_删一条调校_重放回到正确行数()
    {
        (string db, MySQLHelper sql) = await _fixture.CreateFreshDatabaseAsync();
        string originalRootPath = PluginConfig.RootPath;
        string tempCfgDir = Path.Combine(Path.GetTempPath(), "forza-mig-replay-" + Guid.NewGuid().ToString("N"));
        string cs = BuildCs(_fixture, db);
        try
        {
            PluginConfig.RootPath = tempCfgDir;

            await ExecuteRawAsync(cs, OldVehiclesDdl);
            await ExecuteRawAsync(cs, @"INSERT INTO `Vehicles`
                (Manufacturer, Model, Year, Class, MatchType, Drivetrain, Attribute, TireCompound, SurfaceCondition, Strength, Tune, Remarks, LapTime, ExtData)
                VALUES
                ('Honda', 'Civic', '2022', 'A', 'Road', 'FWD', '操控', '半热熔', '干', 'S', 'tune_abc', '好开', '1:02.3', NULL),
                ('Honda', 'Civic', '2022', 'A', 'Road', 'AWD', '均衡', '全热熔', '湿地', 'A', 'tune_xyz', '稳', '1:03.1', NULL),
                ('Toyota', 'Supra', '1998', 'S1', 'Road', 'RWD', '动力', '半热熔', '干', 'S', 'tune_qrs', '漂逸', '0:58.0', NULL)");

            SQLTableInitializeService svc = new(NullLogger<SQLTableInitializeService>.Instance, sql);

            // 第一次迁移（正常完成）
            svc.EnsureForzaTuneSchema();

            long afterFirst = await ScalarAsync(cs, "SELECT COUNT(*) FROM `VehicleTunes` WHERE SourceFileId='migration'");
            afterFirst.Should().Be(3, "第一次迁移应产生 3 条 migration 调校");

            // 模拟半成品状态：删掉 1 条 migration 调校
            await ExecuteRawAsync(cs,
                "DELETE FROM `VehicleTunes` WHERE SourceFileId='migration' LIMIT 1");

            long afterDelete = await ScalarAsync(cs, "SELECT COUNT(*) FROM `VehicleTunes` WHERE SourceFileId='migration'");
            afterDelete.Should().Be(2, "手动删除后应只剩 2 条 migration 调校");

            // 强制清除版本标记，模拟重启时"迁移标记未持久化"
            PluginConfig schemaCfg = new("forza", "schema");
            schemaCfg.LoadConfig();
            schemaCfg.Remove("version");
            schemaCfg.SaveConfig();

            // 再次调用：应检测到 tuneMig(2) != legacyCount(3)，触发完整重放
            Action replay = () => svc.EnsureForzaTuneSchema();
            replay.Should().NotThrow("重放调用不应抛出异常");

            long afterReplay = await ScalarAsync(cs, "SELECT COUNT(*) FROM `VehicleTunes` WHERE SourceFileId='migration'");
            afterReplay.Should().Be(3, "重放后 migration 调校应恢复到 3 条，不应因 backup 表存在而跳过");

            // 版本标记应再次置位
            PluginConfig cfgCheck = new("forza", "schema");
            cfgCheck.LoadConfig();
            (cfgCheck.Get<string>("version") ?? "").Should().Be("2", "重放完成后版本标记应再次置 '2'");
        }
        finally
        {
            PluginConfig.RootPath = originalRootPath;
            sql.Dispose();
            try { if (Directory.Exists(tempCfgDir)) Directory.Delete(tempCfgDir, recursive: true); } catch { }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 测试 4：旧表 Id 不连续（PR #22 线上事故回归）——
    // 迁移必须成功，且外键绑定新 Vehicles 而非随 RENAME 跟到 legacy 表
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnsureForzaTuneSchema_旧表Id不连续_迁移成功且外键指向新Vehicles()
    {
        (string db, MySQLHelper sql) = await _fixture.CreateFreshDatabaseAsync();
        string originalRootPath = PluginConfig.RootPath;
        string tempCfgDir = Path.Combine(Path.GetTempPath(), "forza-mig-gapid-" + Guid.NewGuid().ToString("N"));
        string cs = BuildCs(_fixture, db);
        try
        {
            PluginConfig.RootPath = tempCfgDir;

            await ExecuteRawAsync(cs, OldVehiclesDdl);
            await ExecuteRawAsync(cs, SeedGappedIdRows);

            SQLTableInitializeService svc = new(NullLogger<SQLTableInitializeService>.Instance, sql);
            Action migrate = () => svc.EnsureForzaTuneSchema();
            migrate.Should().NotThrow("旧表 Id 与新车型主表 Id 无交集时迁移也必须成功");

            (await FkReferencedTableAsync(cs)).Should().BeEquivalentTo("Vehicles",
                "fk_tune_vehicle 必须引用新 Vehicles 车型主表，而非 Vehicles_legacy_v1");

            long migrationTunes = await ScalarAsync(cs, "SELECT COUNT(*) FROM `VehicleTunes` WHERE SourceFileId='migration'");
            migrationTunes.Should().Be(3, "3 条旧记录应全部回填成功");

            long vehicleCount = await ScalarAsync(cs, "SELECT COUNT(*) FROM `Vehicles`");
            vehicleCount.Should().Be(2, "Honda Civic 2022 应合并为 1 条车型");

            PluginConfig cfg = new("forza", "schema");
            cfg.LoadConfig();
            (cfg.Get<string>("version") ?? "").Should().Be("2", "迁移完成后版本标记应置 '2'");
        }
        finally
        {
            PluginConfig.RootPath = originalRootPath;
            sql.Dispose();
            try { if (Directory.Exists(tempCfgDir)) Directory.Delete(tempCfgDir, recursive: true); } catch { }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 测试 5：外键误绑修复——历史坏状态（fk 指向 legacy、仅迁移行）应自动修复并完成回填
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnsureForzaTuneSchema_外键误指向legacy且仅迁移行_自动修复并完成回填()
    {
        (string db, MySQLHelper sql) = await _fixture.CreateFreshDatabaseAsync();
        string originalRootPath = PluginConfig.RootPath;
        string tempCfgDir = Path.Combine(Path.GetTempPath(), "forza-mig-fkfix-" + Guid.NewGuid().ToString("N"));
        string cs = BuildCs(_fixture, db);
        try
        {
            PluginConfig.RootPath = tempCfgDir;

            await SetupMisboundFkStateAsync(cs);

            // 前置校验：确认坏状态复现成功（外键确实跟随 RENAME 指向了 legacy 表）
            (await FkReferencedTableAsync(cs)).Should().BeEquivalentTo("Vehicles_legacy_v1",
                "测试前置：RENAME 后外键应误指向 Vehicles_legacy_v1，否则坏状态未复现");

            SQLTableInitializeService svc = new(NullLogger<SQLTableInitializeService>.Instance, sql);
            Action migrate = () => svc.EnsureForzaTuneSchema();
            migrate.Should().NotThrow("表内仅迁移哨兵行时应自动修复外键并完成回填");

            (await FkReferencedTableAsync(cs)).Should().BeEquivalentTo("Vehicles",
                "修复后 fk_tune_vehicle 应引用新 Vehicles 车型主表");

            long migrationTunes = await ScalarAsync(cs, "SELECT COUNT(*) FROM `VehicleTunes` WHERE SourceFileId='migration'");
            migrationTunes.Should().Be(3, "修复后应完整重放回填 3 条调校");

            long cacheTableExists = await ScalarAsync(cs,
                "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='VehicleTuneCache'");
            cacheTableExists.Should().Be(1, "恢复路径应补齐缺失的 VehicleTuneCache 表");

            PluginConfig cfg = new("forza", "schema");
            cfg.LoadConfig();
            (cfg.Get<string>("version") ?? "").Should().Be("2", "修复完成后版本标记应置 '2'");
        }
        finally
        {
            PluginConfig.RootPath = originalRootPath;
            sql.Dispose();
            try { if (Directory.Exists(tempCfgDir)) Directory.Delete(tempCfgDir, recursive: true); } catch { }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 测试 6：外键误绑拒绝——坏状态且存在非迁移调校时，拒绝自动修复并抛出异常
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnsureForzaTuneSchema_外键误指向legacy且存在非迁移调校_拒绝修复并抛出()
    {
        (string db, MySQLHelper sql) = await _fixture.CreateFreshDatabaseAsync();
        string originalRootPath = PluginConfig.RootPath;
        string tempCfgDir = Path.Combine(Path.GetTempPath(), "forza-mig-fkrefuse-" + Guid.NewGuid().ToString("N"));
        string cs = BuildCs(_fixture, db);
        try
        {
            PluginConfig.RootPath = tempCfgDir;

            await SetupMisboundFkStateAsync(cs);

            // 插入 1 条非迁移调校（VehicleId=501 在 legacy 表中存在，满足误绑的外键）
            await ExecuteRawAsync(cs, @"INSERT INTO `VehicleTunes`
                (VehicleId, Class, MatchType, SourceFileId, SourceIdentity, SourceIdentitySeq, IsManual, TuneStatus)
                VALUES (501, 'A', 'Road', 'DSTESTFILE', 'honda|civic|2022|a|road', 0, 0, '正常')");

            SQLTableInitializeService svc = new(NullLogger<SQLTableInitializeService>.Instance, sql);
            Action migrate = () => svc.EnsureForzaTuneSchema();
            migrate.Should().Throw<InvalidOperationException>("存在非迁移调校时不得自动删数据修复")
                .WithMessage("*非迁移*");

            // 数据保持原样：非迁移行未被删除，外键未被改动，版本标记未置位
            long nonMigTunes = await ScalarAsync(cs, "SELECT COUNT(*) FROM `VehicleTunes` WHERE SourceFileId<>'migration'");
            nonMigTunes.Should().Be(1, "拒绝修复时不得删除非迁移调校");

            (await FkReferencedTableAsync(cs)).Should().BeEquivalentTo("Vehicles_legacy_v1",
                "拒绝修复时不得改动外键");

            PluginConfig cfg = new("forza", "schema");
            cfg.LoadConfig();
            (cfg.Get<string>("version") ?? "").Should().NotBe("2", "拒绝修复时不得置版本标记");
        }
        finally
        {
            PluginConfig.RootPath = originalRootPath;
            sql.Dispose();
            try { if (Directory.Exists(tempCfgDir)) Directory.Delete(tempCfgDir, recursive: true); } catch { }
        }
    }
}
