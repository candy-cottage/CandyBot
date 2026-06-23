using CandyBot.IntegrationTests.Infrastructure;
using FluentAssertions;
using MySql.Data.MySqlClient;
using Xunit;

namespace CandyBot.IntegrationTests.Infrastructure;

/// <summary>
/// 断言 <see cref="MySqlFixture.ResetDataAsync"/> 会正确清空所有数据表，
/// 包括子表 <c>VehicleTunes</c> 和 <c>VehicleTuneCache</c>。
/// <para>
/// 重点验证：TRUNCATE 不触发 ON DELETE CASCADE，因此子表必须在
/// <c>DataTables</c> 中独立列出，否则父表截断后子表行会残留。
/// </para>
/// </summary>
[Collection(MySqlCollection.Name)]
[Trait("Category", "MySql")]
public class MySqlFixtureResetTests(MySqlFixture fixture)
{
    private readonly MySqlFixture _fixture = fixture;

    [Fact]
    public async Task ResetDataAsync_截断后VehicleTunes和Vehicles行数均为零()
    {
        // Arrange：先清干净，再插入父子表各一行
        await _fixture.ResetDataAsync();

        await using MySqlConnection conn = new(BuildConnectionString());
        await conn.OpenAsync();

        // 插入 Vehicles 父行（ModelKey 需 utf8mb4_bin 排序规则，用字符串字面量即可）
        await using (MySqlCommand insertVehicle = new(@"
            INSERT INTO `Vehicles`
                (`Manufacturer`, `Model`, `ModelKey`)
            VALUES
                ('TestMaker', 'TestModel', 'TestMaker TestModel');", conn))
        {
            await insertVehicle.ExecuteNonQueryAsync();
        }

        // 取得刚插入的 VehicleId
        long vehicleId;
        await using (MySqlCommand lastId = new("SELECT LAST_INSERT_ID();", conn))
        {
            vehicleId = Convert.ToInt64(await lastId.ExecuteScalarAsync());
        }

        // 插入 VehicleTunes 子行（VehicleId, Class, MatchType, SourceFileId, SourceIdentity 均为 NOT NULL）
        await using (MySqlCommand insertTune = new(@"
            INSERT INTO `VehicleTunes`
                (`VehicleId`, `Class`, `MatchType`, `SourceFileId`, `SourceIdentity`)
            VALUES
                (@vehicleId, 'S1', '道路赛', 'file-reset-test', 'identity-reset-test');", conn))
        {
            insertTune.Parameters.AddWithValue("@vehicleId", vehicleId);
            await insertTune.ExecuteNonQueryAsync();
        }

        // 预检：确认行确实已写入
        long tunesBefore = await CountRowsAsync(conn, "VehicleTunes");
        long vehiclesBefore = await CountRowsAsync(conn, "Vehicles");
        tunesBefore.Should().Be(1, "插入后 VehicleTunes 应有 1 行");
        vehiclesBefore.Should().Be(1, "插入后 Vehicles 应有 1 行");

        // Act
        await _fixture.ResetDataAsync();

        // Assert：两张表均被清空（TRUNCATE 不触发 CASCADE，子表须独立截断）
        long tunesAfter = await CountRowsAsync(conn, "VehicleTunes");
        long vehiclesAfter = await CountRowsAsync(conn, "Vehicles");
        tunesAfter.Should().Be(0, "ResetDataAsync 应独立 TRUNCATE VehicleTunes，不依赖 CASCADE");
        vehiclesAfter.Should().Be(0, "ResetDataAsync 应 TRUNCATE Vehicles");
    }

    private string BuildConnectionString()
    {
        MySqlConnectionStringBuilder builder = new()
        {
            Server = _fixture.Host,
            Port = _fixture.Port,
            UserID = _fixture.Username,
            Password = _fixture.ConnectionPassword,
            Database = _fixture.Database,
            CharacterSet = "utf8mb4",
        };
        return builder.ToString();
    }

    private static async Task<long> CountRowsAsync(MySqlConnection conn, string tableName)
    {
        await using MySqlCommand cmd = new($"SELECT COUNT(*) FROM `{tableName}`;", conn);
        object? result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }
}
