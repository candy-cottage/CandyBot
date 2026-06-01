using System.Data;
using CandyBot.IntegrationTests.Infrastructure;
using CandyCottage.CandyBot.Utilities;
using FluentAssertions;
using Milimoe.FunGame.Core.Api.Utility;
using Milimoe.FunGame.Core.Entity;
using MySql.Data.MySqlClient;
using ProjectRedbud.FunGame.SQLQueryExtension;
using Xunit;

namespace CandyBot.IntegrationTests.Sql;

/// <summary>
/// B12 回归测试：验证同步版 <see cref="SQLHelperExtension.ExecuteDataRowForUpdate"/> 在 MySQL 上
/// 确实追加了 <c>FOR UPDATE</c> 行级写锁。
///
/// <para>原 Bug：同步版误调用 <c>ExecuteDataSet</c>（不加锁），异步版才正确加锁。修复后两者一致。</para>
///
/// <para><b>测试原理（确定性）：</b></para>
/// <list type="number">
///   <item>helper A 在事务中对某 Users 行执行 <c>ExecuteDataRowForUpdate</c>。
///   由于事务未提交（<c>localTransaction==false</c> 路径），<c>SELECT ... FOR UPDATE</c> 持有的行写锁会一直保持，直到 A 提交/回滚。</item>
///   <item>另一条独立连接 B 先 <c>SET SESSION innodb_lock_wait_timeout=1</c>，再 UPDATE 同一行。
///   修复后该行被 A 锁住，B 必然在 ~1 秒后抛出 <c>1205</c>（Lock wait timeout exceeded）。
///   若没有修复（不加锁），B 的 UPDATE 会立即成功、不抛异常——本测试因此能够区分两种实现。</item>
/// </list>
/// 锁等待超时由 MySQL 服务器强制，因此该测试是确定性的，不依赖测试线程的休眠时序。
/// </summary>
[Collection(MySqlCollection.Name)]
[Trait("Category", "MySql")]
public class RowLockTests(MySqlFixture fixture)
{
    private readonly MySqlFixture _fixture = fixture;

    private const int LockWaitTimeoutErrorCode = 1205; // ER_LOCK_WAIT_TIMEOUT

    [Fact]
    public async Task ExecuteDataRowForUpdate_持有行写锁阻塞其他连接更新()
    {
        await _fixture.ResetDataAsync();

        // 种子一个 Users 行
        const string username = "rowlock-user";
        string autoKey = username.Encrypt(username);
        long uid;
        using (MySQLHelper seed = _fixture.CreateHelper())
        {
            seed.RegisterUser(username, "pwd", "", "", autoKey);
            User user = seed.GetUserByUsernameAndAutoKey(username, autoKey, loadInventory: true)
                ?? throw new InvalidOperationException("种子用户创建失败。");
            uid = user.Id;
        }

        // helper A：开启事务并对该行加 FOR UPDATE 写锁（修复后生效）。
        using MySQLHelper helperA = _fixture.CreateHelper();
        helperA.NewTransaction();
        helperA.Parameters["@id"] = uid;
        DataRow? locked = helperA.ExecuteDataRowForUpdate("SELECT * FROM `Users` WHERE `Id` = @id");
        locked.Should().NotBeNull("应能选中被锁定的行");
        Convert.ToInt64(locked!["Id"]).Should().Be(uid);

        try
        {
            // 连接 B：独立连接，1 秒锁等待超时，尝试更新同一行。
            await using MySqlConnection connB = new(BuildConnectionString());
            await connB.OpenAsync();
            await using (MySqlCommand setTimeout = new("SET SESSION innodb_lock_wait_timeout = 1;", connB))
            {
                await setTimeout.ExecuteNonQueryAsync();
            }

            await using MySqlCommand update = new("UPDATE `Users` SET `Nickname` = 'blocked' WHERE `Id` = @id;", connB);
            update.Parameters.AddWithValue("@id", uid);

            // 修复后：A 持锁，B 必在约 1 秒后抛 1205 锁等待超时。
            Func<Task> act = async () => await update.ExecuteNonQueryAsync();
            (await act.Should().ThrowAsync<MySqlException>("A 持有行写锁，B 的更新应被阻塞并超时"))
                .Which.Number.Should().Be(LockWaitTimeoutErrorCode, "应为锁等待超时（1205）");
        }
        finally
        {
            // 释放锁
            helperA.Rollback();
        }
    }

    [Fact]
    public async Task 无锁时其他连接更新可立即成功_对照组()
    {
        // 对照组：没有任何事务持锁时，B 的同一更新应立即成功（证明上面的超时确由行锁导致，而非环境天然阻塞）。
        await _fixture.ResetDataAsync();

        const string username = "rowlock-control";
        string autoKey = username.Encrypt(username);
        long uid;
        using (MySQLHelper seed = _fixture.CreateHelper())
        {
            seed.RegisterUser(username, "pwd", "", "", autoKey);
            User user = seed.GetUserByUsernameAndAutoKey(username, autoKey, loadInventory: true)!;
            uid = user.Id;
        }

        await using MySqlConnection connB = new(BuildConnectionString());
        await connB.OpenAsync();
        await using (MySqlCommand setTimeout = new("SET SESSION innodb_lock_wait_timeout = 1;", connB))
        {
            await setTimeout.ExecuteNonQueryAsync();
        }
        await using MySqlCommand update = new("UPDATE `Users` SET `Nickname` = 'ok' WHERE `Id` = @id;", connB);
        update.Parameters.AddWithValue("@id", uid);

        int affected = await update.ExecuteNonQueryAsync();
        affected.Should().Be(1, "无人持锁时更新应立即成功");
    }

    private string BuildConnectionString()
    {
        MySqlConnectionStringBuilder builder = new()
        {
            Server = _fixture.Host,
            Port = _fixture.Port,
            Database = _fixture.Database,
            UserID = _fixture.Username,
            Password = _fixture.ConnectionPassword,
            CharacterSet = "utf8mb4",
        };
        return builder.ToString();
    }
}
