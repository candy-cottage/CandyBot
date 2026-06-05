using System.Net;
using System.Net.Http.Headers;
using CandyBot.IntegrationTests.Infrastructure;
using CandyCottage.CandyBot.Utilities;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Milimoe.FunGame.Core.Api.Transmittal;
using Milimoe.FunGame.Core.Api.Utility;
using Milimoe.FunGame.Core.Entity;
using ProjectRedbud.FunGame.SQLQueryExtension;
using Xunit;

namespace CandyBot.IntegrationTests.Auth;

/// <summary>
/// 针对 <c>ApiTokensBearerHandler</c> + <c>[Authorize(Roles="admin,op")]</c> 的真实 MySQL
/// 端到端鉴权集成测试，并作为 B11 修复的回归守卫。
///
/// <para><b>鉴权原理（来自阅读 Utilities/ApiTokensBearerHandler.cs 与 Utilities/CandyBotConfig.cs）：</b></para>
/// <list type="bullet">
///   <item>Handler 读取 <c>Authorization: Bearer &lt;token&gt;</c>；无标头 → NoResult，
///   非法标头/找不到记录 → Fail（最终 401）。</item>
///   <item>命中查询：<c>Select Users.* From Users Join ApiTokens On
///   ApiTokens.Reference1 = Users.Username Where ApiTokens.SecretKey = @SecretKey</c>，
///   其中 <c>@SecretKey = token.Encrypt(config.ApiTokenSalt)</c>。</item>
///   <item>角色映射：IsAdmin → "admin"，否则 IsOperator → "op"，否则 "user"。</item>
/// </list>
///
/// <para><b>B11：</b> <c>BettingController.GetBets</c> 旧代码用 <c>User.IsInRole("Admin"/"Operator")</c>
/// （大写）做手动检查，与 handler 写入的小写 "admin"/"op" 不符，导致已认证管理员/运营被 403。
/// 修复后管理员/运营 token 访问 <c>GET /betting/bets</c> 应得 200。下方
/// <see cref="GetBets_管理员Token_返回200_B11回归守卫"/> 即对此 load-bearing：
/// 若把大小写改回去，它会因收到 403 而失败。</para>
///
/// <para><b>种子方式：</b> 用 FunGame SQL 扩展 <c>RegisterUser</c> /
/// <c>GetUserByUsernameAndAutoKey</c> / <c>UpdateUser</c> 建用户并设 IsAdmin/IsOperator
/// （镜像 Phase 6b 与 SQLTableInitializeService 的做法）；ApiTokens 表无专用扩展助手，
/// 故用 <see cref="MySQLHelper"/> 直接参数化 INSERT 一行（Reference1=用户名，
/// SecretKey=rawToken.Encrypt(salt)，TokenID 唯一）。盐值从工厂解析的应用
/// <see cref="CandyBotConfig"/> 取（不硬编码）。</para>
/// </summary>
[Collection(MySqlCollection.Name)]
[Trait("Category", "MySql")]
public sealed class ApiTokenAuthTests : IAsyncLifetime
{
    private readonly MySqlFixture _fixture;
    private readonly MySqlWebApplicationFactory _factory;

    // 各角色使用的原始 token（明文）。鉴权用 token.Encrypt(salt) 入库为 SecretKey，
    // 请求头携带原始 token。
    private const string AdminRawToken = "admin-raw-token-6d";
    private const string OperatorRawToken = "operator-raw-token-6d";

    public ApiTokenAuthTests(MySqlFixture fixture)
    {
        _fixture = fixture;
        _factory = new MySqlWebApplicationFactory(
            fixture.Host, fixture.Port, fixture.Database, fixture.Username, fixture.ConnectionPassword);
    }

    public async Task InitializeAsync()
    {
        await _fixture.ResetDataAsync();

        // 从工厂解析的应用 CandyBotConfig 取盐值（不硬编码），保证与 handler 计算一致。
        string salt;
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            salt = scope.ServiceProvider.GetRequiredService<CandyBotConfig>().ApiTokenSalt;
        }

        SeedUserWithToken("auth-admin", isAdmin: true, isOperator: true, rawToken: AdminRawToken, salt: salt, tokenId: "tok-admin-6d");
        SeedUserWithToken("auth-op", isAdmin: false, isOperator: true, rawToken: OperatorRawToken, salt: salt, tokenId: "tok-op-6d");
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 注册用户、设角色并写入对应 ApiTokens 行。每步取全新 helper 并 Dispose。
    /// </summary>
    private void SeedUserWithToken(string username, bool isAdmin, bool isOperator, string rawToken, string salt, string tokenId)
    {
        string autoKey = username.Encrypt(username);
        using (MySQLHelper sql = _fixture.CreateHelper())
        {
            sql.RegisterUser(username, "pwd", "", "", autoKey);
            User user = sql.GetUserByUsernameAndAutoKey(username, autoKey, loadInventory: true)
                ?? throw new InvalidOperationException("种子用户创建失败。");
            user.IsAdmin = isAdmin;
            user.IsOperator = isOperator;
            sql.UpdateUser(user);
        }

        // ApiTokens 无专用 SQL 扩展助手，直接参数化 INSERT。
        // SecretKey = rawToken.Encrypt(salt)，与 handler 的查询条件一致。
        using (MySQLHelper sql = _fixture.CreateHelper())
        {
            sql.Parameters["@TokenID"] = tokenId;
            sql.Parameters["@SecretKey"] = rawToken.Encrypt(salt);
            sql.Parameters["@Reference1"] = username;
            sql.Parameters["@Reference2"] = "";
            sql.Execute("INSERT INTO ApiTokens (TokenID, SecretKey, Reference1, Reference2) VALUES (@TokenID, @SecretKey, @Reference1, @Reference2)");
            sql.Success.Should().BeTrue("应成功写入 ApiTokens 行");
        }
    }

    private HttpClient CreateClient(string? bearerToken)
    {
        HttpClient client = _factory.CreateClient();
        if (bearerToken is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }
        return client;
    }

    [Fact]
    public async Task GetBets_无效Token_返回401()
    {
        HttpClient client = CreateClient("not-a-real-token");

        HttpResponseMessage resp = await client.GetAsync("/betting/bets");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// B11 load-bearing 守卫：管理员 token 访问 admin/op 端点应得 200。
    /// 旧代码用大写 "Admin"/"Operator" 检查，会对小写角色声明返回 403——本断言因此会失败。
    /// </summary>
    [Fact]
    public async Task GetBets_管理员Token_返回200_B11回归守卫()
    {
        HttpClient client = CreateClient(AdminRawToken);

        HttpResponseMessage resp = await client.GetAsync("/betting/bets");

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "B11 修复后，已认证管理员（角色声明 'admin'）应能访问 GET /betting/bets；若回归为大写检查会得 403");
    }

    [Fact]
    public async Task GetBets_删除全部令牌后_原Token立即返回401()
    {
        HttpClient client = CreateClient(AdminRawToken);

        HttpResponseMessage first = await client.GetAsync("/betting/bets");
        first.StatusCode.Should().Be(HttpStatusCode.OK, "首次访问用于确认该 bearer token 原本有效");

        using (MySQLHelper sql = _fixture.CreateHelper())
        {
            sql.Execute("DELETE FROM ApiTokens");
            sql.Success.Should().BeTrue("测试需要模拟全部 bearer 令牌已被删除");
        }

        HttpResponseMessage resp = await client.GetAsync("/betting/bets");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "ApiTokens 表中已无任何令牌，旧 bearer token 不应继续通过 [Authorize] 控制器");
    }

    [Fact]
    public async Task GetBets_运营Token_返回200()
    {
        HttpClient client = CreateClient(OperatorRawToken);

        HttpResponseMessage resp = await client.GetAsync("/betting/bets");

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "运营角色（'op'）按 [Authorize(Roles=\"admin,op\")] 应可访问 admin/op 端点");
    }

    /// <summary>
    /// 角色映射可推广到其他 admin/op 端点：管理员 token 访问 DELETE /forza/vehicles/{id}
    /// 不应是 401/403（具体业务结果不重要，关键是通过了鉴权与授权闸门）。
    /// </summary>
    [Fact]
    public async Task 管理员Token_访问其它AdminOp端点_未被401或403()
    {
        HttpClient client = CreateClient(AdminRawToken);

        HttpResponseMessage resp = await client.DeleteAsync("/forza/vehicles/999999");

        resp.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        resp.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }
}
