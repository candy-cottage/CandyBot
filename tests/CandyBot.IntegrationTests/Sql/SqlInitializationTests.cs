using CandyBot.IntegrationTests.Infrastructure;
using CandyCottage.CandyBot.Services.SystemServices;
using CandyCottage.CandyBot.Utilities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Milimoe.FunGame.Core.Api.Transmittal;
using Milimoe.FunGame.Core.Api.Utility;
using Milimoe.FunGame.Core.Entity;
using ProjectRedbud.FunGame.SQLQueryExtension;
using Xunit;

namespace CandyBot.IntegrationTests.Sql;

/// <summary>
/// 针对 <see cref="SQLTableInitializeService.Initialize"/> 的端到端验证：
/// 在一个真实 MySQL 容器中、从一个<b>全新空库</b>出发，确认建表脚本能成功执行、
/// 首位管理员被正确创建、并写入了初始化标记。
///
/// <para>隔离手段（两层）：</para>
/// <list type="number">
///   <item>
///     <b>数据库隔离</b>：通过 <see cref="MySqlFixture.CreateFreshDatabaseAsync"/> 在共享容器上
///     新建一个唯一命名的空库再测，<see cref="SQLTableInitializeService"/> 内部的
///     <c>DROP TABLE</c> 因此不会冲掉夹具准备的共享 schema 库。
///   </item>
///   <item>
///     <b>PluginConfig 隔离</b>：<c>Initialize</c> 会向 <c>PluginConfig("sqlconfig","initialization")</c>
///     写 admin/time 键，而 <c>PluginConfig.RootPath</c> 是进程级静态全局。测试在 try/finally 中
///     把它指向唯一临时目录，结束后恢复并清理，从而不污染真实 <c>configs/</c>，
///     也保证 <c>WillInitialize()</c> 在测试开始时确为 true。
///   </item>
/// </list>
/// </summary>
[Collection(MySqlCollection.Name)]
[Trait("Category", "MySql")]
public class SqlInitializationTests(MySqlFixture fixture)
{
    private readonly MySqlFixture _fixture = fixture;

    [Fact]
    public async Task Initialize_应在空库上建表并提升首位管理员()
    {
        const string adminOpenId = "test-openid-first-admin";
        string autoKey = adminOpenId.Encrypt(adminOpenId);

        (string freshDb, MySQLHelper sql) = await _fixture.CreateFreshDatabaseAsync();
        string originalRootPath = PluginConfig.RootPath;
        string tempConfigDir = Path.Combine(Path.GetTempPath(), "candybot-init-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            PluginConfig.RootPath = tempConfigDir;

            SQLTableInitializeService service = new(NullLogger<SQLTableInitializeService>.Instance, sql);

            // 空库 + 干净 PluginConfig：初始化前应判定为“需要初始化”。
            service.WillInitialize().Should().BeTrue("空库且 admin 键未写入时应需要初始化");

            service.Initialize(adminOpenId, out string msg);

            // 1. 初始化报告成功（含成功文案，不含错误文案）。
            msg.Should().Contain("系统初始化完成");
            msg.Should().NotContain("系统初始化遇到错误");

            // 2. 关键表已建立。逐张查询应能成功（NotFound 表示空表但表存在，SQLError 才是出错）。
            foreach (string table in new[]
                     {
                         "Users", "Inventories", "ApiTokens",
                         "betting_events", "betting_matches", "betting_bet_records",
                         "Vehicles", "NicknameChangeRequests",
                     })
            {
                using MySQLHelper probe = _fixture.CreateHelper(freshDb);
                probe.ExecuteDataSet($"SELECT * FROM `{table}` LIMIT 1;");
                probe.Result.Should().NotBe(Milimoe.FunGame.Core.Library.Constant.SQLResult.SQLError,
                    "表 {0} 应已被建表脚本创建", table);
            }

            // 3. 首位管理员被正确创建并提升。
            using (MySQLHelper userSql = _fixture.CreateHelper(freshDb))
            {
                User? admin = userSql.GetUserByUsernameAndAutoKey(adminOpenId, autoKey, loadInventory: true);
                admin.Should().NotBeNull("初始化应注册首位管理员用户");
                admin!.IsAdmin.Should().BeTrue();
                admin.IsOperator.Should().BeTrue();
                admin.NickName.Should().Be("Candy#Admin");
                admin.Inventory.Credits.Should().Be(50000);
                admin.Inventory.Materials.Should().Be(1000);
            }

            // 4. 初始化标记已写入：WillInitialize 由 true 变 false（admin 键已落盘）。
            service.WillInitialize().Should().BeFalse("初始化完成后 admin 键已写入，不应再判定为需要初始化");
        }
        finally
        {
            PluginConfig.RootPath = originalRootPath;
            sql.Dispose();
            try { if (Directory.Exists(tempConfigDir)) Directory.Delete(tempConfigDir, recursive: true); } catch { }
        }
    }
}
