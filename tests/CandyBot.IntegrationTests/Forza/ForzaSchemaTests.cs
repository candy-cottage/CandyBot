using CandyBot.IntegrationTests.Infrastructure;
using CandyCottage.CandyBot.Utilities;
using FluentAssertions;
using Milimoe.FunGame.Core.Api.Transmittal;
using Milimoe.FunGame.Core.Library.Constant;
using Xunit;

namespace CandyBot.IntegrationTests.Forza;

/// <summary>
/// 断言 forza.sql 重构后的三表 schema 结构正确：
/// <list type="bullet">
///   <item><c>Vehicles</c> 拥有 <c>ModelKey</c> 列和 <c>uq_modelkey</c> 唯一索引。</item>
///   <item><c>VehicleTunes</c> 拥有覆盖 (SourceFileId, SourceIdentity, SourceIdentitySeq) 的
///   唯一索引 <c>uq_source_identity</c>，以及指向 Vehicles(Id) 且 DELETE_RULE='CASCADE' 的外键
///   <c>fk_tune_vehicle</c>。</item>
///   <item><c>VehicleTuneCache</c> 拥有 <c>uq_cache_tune</c> 唯一索引（覆盖 TuneId）。</item>
///   <item><c>Vehicles.ModelKey</c> 与 <c>VehicleTunes.SourceIdentity</c> 的 COLLATION_NAME
///   均为 <c>utf8mb4_bin</c>。</item>
/// </list>
/// <para>TDD：测试先于 forza.sql 改动提交，在旧单表 schema（无 ModelKey）时应 RED，
/// 在三表新 schema 下应 GREEN。</para>
/// <para>每个测试用 <see cref="MySqlFixture.CreateFreshDatabaseAsync"/> 创建独立空库，
/// 然后顺序执行四个建表脚本（与 <see cref="MySqlFixture"/> 的 shared schema 库保持一致），
/// 再查询 information_schema 断言结构。</para>
/// </summary>
[Collection(MySqlCollection.Name)]
[Trait("Category", "MySql")]
public class ForzaSchemaTests(MySqlFixture fixture)
{
    private readonly MySqlFixture _fixture = fixture;

    private static readonly string[] ScriptOrder = ["fungame.sql", "betting.sql", "candybot.sql", "forza.sql"];

    /// <summary>
    /// 在 freshDb 上运行四个建表脚本，初始化完整 schema（含新三表）。
    /// 遇到 SQLError 立即抛出，使测试以清晰错误失败而非后续断言失败。
    /// </summary>
    private static void RunSchemaScripts(SQLHelper sql)
    {
        string scriptsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts");
        foreach (string scriptName in ScriptOrder)
        {
            sql.ExecuteSqlFile(Path.Combine(scriptsDir, scriptName));
            if (sql.Result == SQLResult.SQLError)
                throw new InvalidOperationException($"Schema script failed: {scriptName}", sql.LastException);
        }
    }

    [Fact]
    public async Task Vehicles_拥有ModelKey列和uq_modelkey唯一索引()
    {
        (string db, MySQLHelper sql) = await _fixture.CreateFreshDatabaseAsync();
        try
        {
            RunSchemaScripts(sql);

            // 确认 ModelKey 列存在
            sql.Parameters["@db"] = db;
            sql.Parameters["@table"] = "Vehicles";
            sql.Parameters["@col"] = "ModelKey";
            sql.ExecuteDataSet(@"
                SELECT COLUMN_NAME
                FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = @db
                  AND TABLE_NAME   = @table
                  AND COLUMN_NAME  = @col");

            sql.DataSet.Tables[0].Rows.Count
                .Should().Be(1, "Vehicles 表应存在 ModelKey 列");

            // 确认 uq_modelkey 唯一索引存在（Non_unique=0）
            sql.ExecuteDataSet("SHOW INDEX FROM `Vehicles` WHERE Key_name = 'uq_modelkey'");
            sql.DataSet.Tables[0].Rows.Count
                .Should().BeGreaterThan(0, "Vehicles 表应存在 uq_modelkey 索引");
            Convert.ToInt32(sql.DataSet.Tables[0].Rows[0]["Non_unique"])
                .Should().Be(0, "uq_modelkey 应为唯一索引");
        }
        finally
        {
            sql.Dispose();
        }
    }

    [Fact]
    public async Task VehicleTunes_拥有uq_source_identity唯一索引且覆盖三列()
    {
        (string db, MySQLHelper sql) = await _fixture.CreateFreshDatabaseAsync();
        try
        {
            RunSchemaScripts(sql);

            sql.ExecuteDataSet("SHOW INDEX FROM `VehicleTunes` WHERE Key_name = 'uq_source_identity'");

            var rows = sql.DataSet.Tables[0].Rows;
            rows.Count.Should().Be(3, "uq_source_identity 应覆盖三列（SourceFileId, SourceIdentity, SourceIdentitySeq）");

            // 按 Seq_in_index 排序后校验列名
            var cols = rows.Cast<System.Data.DataRow>()
                .OrderBy(r => Convert.ToInt32(r["Seq_in_index"]))
                .Select(r => r["Column_name"].ToString())
                .ToArray();
            cols.Should().Equal("SourceFileId", "SourceIdentity", "SourceIdentitySeq");

            Convert.ToInt32(rows[0]["Non_unique"]).Should().Be(0, "uq_source_identity 应为唯一索引");
        }
        finally
        {
            sql.Dispose();
        }
    }

    [Fact]
    public async Task VehicleTunes_拥有指向Vehicles的CASCADE外键fk_tune_vehicle()
    {
        (string db, MySQLHelper sql) = await _fixture.CreateFreshDatabaseAsync();
        try
        {
            RunSchemaScripts(sql);

            sql.Parameters["@db"] = db;
            sql.Parameters["@fk"] = "fk_tune_vehicle";
            sql.ExecuteDataSet(@"
                SELECT rc.DELETE_RULE
                FROM information_schema.REFERENTIAL_CONSTRAINTS rc
                JOIN information_schema.TABLE_CONSTRAINTS tc
                  ON tc.CONSTRAINT_NAME = rc.CONSTRAINT_NAME
                 AND tc.CONSTRAINT_SCHEMA = rc.CONSTRAINT_SCHEMA
                WHERE rc.CONSTRAINT_SCHEMA = @db
                  AND rc.CONSTRAINT_NAME   = @fk");

            sql.DataSet.Tables[0].Rows.Count
                .Should().Be(1, "应存在外键 fk_tune_vehicle");
            sql.DataSet.Tables[0].Rows[0]["DELETE_RULE"].ToString()
                .Should().Be("CASCADE", "fk_tune_vehicle 应为 ON DELETE CASCADE");
        }
        finally
        {
            sql.Dispose();
        }
    }

    [Fact]
    public async Task VehicleTuneCache_拥有uq_cache_tune唯一索引覆盖TuneId()
    {
        (string db, MySQLHelper sql) = await _fixture.CreateFreshDatabaseAsync();
        try
        {
            RunSchemaScripts(sql);

            sql.ExecuteDataSet("SHOW INDEX FROM `VehicleTuneCache` WHERE Key_name = 'uq_cache_tune'");

            var rows = sql.DataSet.Tables[0].Rows;
            rows.Count.Should().BeGreaterThan(0, "VehicleTuneCache 应存在 uq_cache_tune 索引");
            Convert.ToInt32(rows[0]["Non_unique"]).Should().Be(0, "uq_cache_tune 应为唯一索引");
            rows[0]["Column_name"].ToString().Should().Be("TuneId", "uq_cache_tune 应覆盖 TuneId 列");
        }
        finally
        {
            sql.Dispose();
        }
    }

    [Fact]
    public async Task Vehicles_ModelKey列和VehicleTunes_SourceIdentity列的COLLATION为utf8mb4_bin()
    {
        (string db, MySQLHelper sql) = await _fixture.CreateFreshDatabaseAsync();
        try
        {
            RunSchemaScripts(sql);

            sql.Parameters["@db"] = db;
            sql.ExecuteDataSet(@"
                SELECT TABLE_NAME, COLUMN_NAME, COLLATION_NAME
                FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = @db
                  AND (
                        (TABLE_NAME = 'Vehicles'      AND COLUMN_NAME = 'ModelKey')
                     OR (TABLE_NAME = 'VehicleTunes'  AND COLUMN_NAME = 'SourceIdentity')
                  )");

            var rows = sql.DataSet.Tables[0].Rows;
            rows.Count.Should().Be(2, "应找到 Vehicles.ModelKey 与 VehicleTunes.SourceIdentity 两列");

            foreach (System.Data.DataRow row in rows)
            {
                string colId = $"{row["TABLE_NAME"]}.{row["COLUMN_NAME"]}";
                row["COLLATION_NAME"].ToString()
                    .Should().Be("utf8mb4_bin", $"{colId} 的 COLLATION 应为 utf8mb4_bin");
            }
        }
        finally
        {
            sql.Dispose();
        }
    }
}
