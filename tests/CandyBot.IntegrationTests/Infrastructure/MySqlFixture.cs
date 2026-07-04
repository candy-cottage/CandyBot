using CandyCottage.CandyBot.Utilities;
using Microsoft.Extensions.Logging.Abstractions;
using Milimoe.FunGame.Core.Api.Transmittal;
using Milimoe.FunGame.Core.Library.Constant;
using MySql.Data.MySqlClient;
using Testcontainers.MySql;
using Xunit;

namespace CandyBot.IntegrationTests.Infrastructure;

/// <summary>
/// 供所有 MySQL 集成测试共享的 Testcontainers 夹具：在整个测试集合的生命周期内
/// 仅启动一个 MySQL 容器，避免每个测试类都付出容器启动的开销。
///
/// 设计要点（后续子阶段 betting/forza/auth 测试会复用本夹具）：
/// <list type="bullet">
///   <item>
///     容器自带一个默认库（<see cref="SchemaDatabase"/>），<see cref="InitializeAsync"/>
///     启动后会按 fungame → betting → candybot → forza 的顺序把四个建表脚本灌入该库，
///     作为“已就绪 schema 库”。需要现成表结构的测试（betting/forza/auth）应基于这个库，
///     用 <see cref="CreateHelper"/> 取一个干净的 <see cref="MySQLHelper"/>。
///   </item>
///   <item>
///     <see cref="SQLTableInitializeService"/> 自己会执行同样的四个脚本（含 DROP TABLE），
///     若让它跑在共享 schema 库上会把 schema 推倒重来。因此初始化测试应改用
///     <see cref="CreateFreshDatabaseAsync"/> 在同一容器上新建一个空库再测，
///     这样 CI 只需一个容器。
///   </item>
/// </list>
///
/// <para>
/// 数据隔离策略：<see cref="MySQLHelper"/> 持有连接与事务状态，因此<b>每个操作/每个测试</b>
/// 都应取一个全新的 helper（<see cref="CreateHelper"/> / 各 fresh-db helper），并自行 Dispose。
/// 会写数据的测试之间靠 <see cref="ResetDataAsync"/>（关闭外键检查后 TRUNCATE 全部数据表）
/// 保持彼此独立——在每个会改数据的测试开始处调用一次即可。
/// </para>
/// </summary>
public sealed class MySqlFixture : IAsyncLifetime
{
    /// <summary>固定使用的 MySQL 镜像。8.0 支持脚本里用到的 json 列、FOREIGN_KEY_CHECKS 与全部 DDL。</summary>
    public const string Image = "mysql:8.0";

    private const string RootUser = "root";
    private const string Password = "candybot-test";
    private const string SchemaDatabase = "candybot_schema";

    /// <summary>四个建表脚本的执行顺序（fungame 必须最先，因为 betting 的外键引用它建立的库）。</summary>
    private static readonly string[] ScriptOrder = ["fungame.sql", "betting.sql", "candybot.sql", "forza.sql"];

    /// <summary>
    /// 已加载完整 schema 的共享库中的数据表，<see cref="ResetDataAsync"/> 会逐一 TRUNCATE。
    /// <para>
    /// 注意：<c>TRUNCATE</c> <b>不会</b>触发 <c>ON DELETE CASCADE</c>（CASCADE 仅在 <c>DELETE</c> 语句时触发），
    /// 因此子表 <c>VehicleTunes</c>、<c>VehicleTuneCache</c> 必须在此独立列出，否则父表
    /// <c>Vehicles</c> 截断后子表行会残留，导致跨测试数据污染。
    /// <c>FOREIGN_KEY_CHECKS=0</c> 仅用于绕过 TRUNCATE 父表时 MySQL 因 FK 引用而拒绝操作的限制。
    /// </para>
    /// </summary>
    private static readonly string[] DataTables =
    [
        "betting_bet_records", "betting_matches", "betting_events",
        "Vehicles", "VehicleTunes", "VehicleTuneCache",
        "ApiTokens", "Users", "Inventories", "NicknameChangeRequests", "Configs",
    ];

    private readonly MySqlContainer _container = new MySqlBuilder()
        .WithImage(Image)
        .WithUsername(RootUser)   // 以 root 登录，才能在同一容器上 CREATE DATABASE 供初始化测试使用
        .WithPassword(Password)
        .WithDatabase(SchemaDatabase)
        .Build();

    /// <summary>容器主机名（映射到宿主，通常是 127.0.0.1）。</summary>
    public string Host => _container.Hostname;

    /// <summary>3306 映射到宿主的端口。</summary>
    public ushort Port => _container.GetMappedPublicPort(3306);

    /// <summary>已就绪 schema 库的库名。</summary>
    public string Database => SchemaDatabase;

    public string Username => RootUser;

    /// <summary>容器的连接口令。</summary>
    public string ConnectionPassword => Password;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // 用一个临时 helper 把四个脚本灌入共享 schema 库。helper 持有连接状态，建完即弃。
        MySQLHelper schemaBuilder = CreateHelper();
        try
        {
            RunSchemaScripts(schemaBuilder);
        }
        finally
        {
            schemaBuilder.Dispose();
        }
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    /// <summary>
    /// 返回一个绑定到共享 schema 库的<b>全新</b> <see cref="MySQLHelper"/>。
    /// 调用方负责 Dispose（helper 内部持有连接/事务）。
    /// </summary>
    public MySQLHelper CreateHelper() => CreateHelper(SchemaDatabase);

    /// <summary>构造一个指向本容器中指定库的 <see cref="SQLConnectionConfig"/>。</summary>
    public SQLConnectionConfig CreateConnectionConfig(string database) => new()
    {
        Mode = SQLMode.MySQL,
        DataSource = Host,
        Port = Port.ToString(),
        DataBase = database,
        User = RootUser,
        Password = Password,
    };

    /// <summary>构造一个绑定到本容器中指定库的全新 <see cref="MySQLHelper"/>。调用方负责 Dispose。</summary>
    public MySQLHelper CreateHelper(string database)
        => new(NullLogger<SQLHelper>.Instance, CreateConnectionConfig(database));

    /// <summary>
    /// 在同一容器上新建一个唯一命名的空库，返回 (库名, 绑定到该库的全新 helper)。
    /// 供需要从“空库”出发的测试（如 <c>SqlInitializationTests</c>）使用，
    /// 避免污染共享 schema 库，同时仍只需一个容器。调用方负责 Dispose 返回的 helper。
    /// </summary>
    public async Task<(string Database, MySQLHelper Helper)> CreateFreshDatabaseAsync()
    {
        string dbName = "candybot_fresh_" + Guid.NewGuid().ToString("N");
        await using (MySqlConnection conn = new(AdminConnectionString()))
        {
            await conn.OpenAsync();
            await using MySqlCommand cmd = new(
                $"CREATE DATABASE `{dbName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        return (dbName, CreateHelper(dbName));
    }

    /// <summary>
    /// 关闭外键检查后逐表 TRUNCATE 共享 schema 库中 <see cref="DataTables"/> 列出的所有数据表，
    /// 使会写数据的测试彼此独立。会改数据的测试应在开头 await 一次。
    /// <para>
    /// <c>FOREIGN_KEY_CHECKS=0</c> 仅用于允许 TRUNCATE 父表（如 <c>Vehicles</c>）；
    /// 子表（<c>VehicleTunes</c>、<c>VehicleTuneCache</c>）必须在 <see cref="DataTables"/> 中独立列出并各自 TRUNCATE，
    /// 因为 TRUNCATE 不触发 ON DELETE CASCADE。
    /// </para>
    /// </summary>
    public async Task ResetDataAsync()
    {
        await using MySqlConnection conn = new(AdminConnectionString(SchemaDatabase));
        await conn.OpenAsync();

        await using (MySqlCommand off = new("SET FOREIGN_KEY_CHECKS = 0;", conn))
        {
            await off.ExecuteNonQueryAsync();
        }
        foreach (string table in DataTables)
        {
            await using MySqlCommand truncate = new($"TRUNCATE TABLE `{table}`;", conn);
            await truncate.ExecuteNonQueryAsync();
        }
        await using (MySqlCommand on = new("SET FOREIGN_KEY_CHECKS = 1;", conn))
        {
            await on.ExecuteNonQueryAsync();
        }
    }

    private string AdminConnectionString(string? database = null)
    {
        MySqlConnectionStringBuilder builder = new()
        {
            Server = Host,
            Port = Port,
            UserID = RootUser,
            Password = Password,
            CharacterSet = "utf8mb4",
        };
        if (database is not null) builder.Database = database;
        return builder.ToString();
    }

    private static void RunSchemaScripts(SQLHelper sql)
    {
        string scriptsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts");
        foreach (string scriptName in ScriptOrder)
        {
            sql.ExecuteSqlFile(Path.Combine(scriptsDir, scriptName));
            if (sql.Result == SQLResult.SQLError)
            {
                throw new InvalidOperationException($"Schema script failed: {scriptName}", sql.LastException);
            }
        }
    }
}

/// <summary>
/// MySQL 测试集合：所有标注 <c>[Collection(MySqlCollection.Name)]</c> 的测试类共享
/// 同一个 <see cref="MySqlFixture"/>（即同一个容器）。
/// </summary>
[CollectionDefinition(MySqlCollection.Name, DisableParallelization = true)]
public class MySqlCollection : ICollectionFixture<MySqlFixture>
{
    public const string Name = "MySql";
}
