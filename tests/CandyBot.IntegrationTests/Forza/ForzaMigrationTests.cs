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
/// <para>三个用例：</para>
/// <list type="number">
///   <item>正常迁移：验证 车型去重、调校行数、哨兵来源标记、legacy 表保留、schema 版本号已置。</item>
///   <item>幂等性：再次调用不报错、行数不变。</item>
///   <item>半成品恢复：强制清除版本标记、删一条 migration 调校行，再次调用须完整重放到正确行数。</item>
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

            // 断言6：VehicleTunes 有 fk_tune_vehicle 外键（与 forza.sql 新建一致）
            long fkCount = await ScalarAsync(cs,
                "SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=DATABASE() AND CONSTRAINT_NAME='fk_tune_vehicle'");
            fkCount.Should().Be(1, "迁移后 VehicleTunes 应有 fk_tune_vehicle 外键");

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
}
