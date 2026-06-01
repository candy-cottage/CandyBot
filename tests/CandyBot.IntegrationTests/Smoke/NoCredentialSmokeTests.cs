using System.Net;
using CandyBot.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CandyBot.IntegrationTests.Smoke;

/// <summary>
/// 无凭据冒烟测试：仅校验 MVC 路由 / 控制器激活 / 鉴权管线，
/// 不依赖 QQ 凭据、腾讯文档凭据、WebSocket 或生产数据库。
/// 命中的端点均不执行 SQL，也不触发出站 HTTP。
/// </summary>
public class NoCredentialSmokeTests : IClassFixture<CandyBotWebApplicationFactory>
{
    private readonly HttpClient _client;

    public NoCredentialSmokeTests(CandyBotWebApplicationFactory factory)
    {
        // 不自动跟随重定向：若 HttpsRedirection 意外产生 307，可被断言捕获而非被吞掉。
        // 注意：测试宿主（TestServer）没有真实监听端口，UseHttpsRedirection 无法
        // 确定 HTTPS 端口，因此它不会产生重定向——AllowAutoRedirect=false 是安全的。
        _client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task Sheet_view_without_fileId_returns_html_page()
    {
        // SheetController.Viewer：fileId 为空时在调用任何腾讯接口前直接渲染视图。
        HttpResponseMessage response = await _client.GetAsync("/sheet/view");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
    }

    [Fact]
    public async Task Forza_vehicles_without_query_returns_bad_request_before_db()
    {
        // ForzaController.GetVehicles：无 name/classType/matchType 时在访问数据库前返回 400。
        HttpResponseMessage response = await _client.GetAsync("/forza/vehicles");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Test_getxml_returns_404_in_production()
    {
        // TestController 标注了 [DevelopmentOnly]：在非 Development 环境（本工厂默认 Production）下，
        // 路由仍注册但被资源过滤器短路为 404，等效于端点不存在。
        HttpResponseMessage response = await _client.GetAsync("/test/getxml");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "TestController 在 Production 下应被 [DevelopmentOnly] 网关返回 404");
    }

    [Fact]
    public async Task Test_gethmacsha256_returns_404_in_production()
    {
        // 同上：[DevelopmentOnly] 在 Production 下使加密工具端点返回 404。
        HttpResponseMessage response = await _client.GetAsync("/test/gethmacsha256?msg=a&key=b");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "TestController 在 Production 下应被 [DevelopmentOnly] 网关返回 404");
    }

    [Fact]
    public async Task Test_gethmacsha512_returns_404_in_production()
    {
        // 同上：[DevelopmentOnly] 在 Production 下使加密工具端点返回 404。
        HttpResponseMessage response = await _client.GetAsync("/test/gethmacsha512?msg=a&key=b");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "TestController 在 Production 下应被 [DevelopmentOnly] 网关返回 404");
    }

    [Fact]
    public async Task Forza_add_vehicle_without_token_is_challenged()
    {
        // ForzaController.AddVehicle 带 [Authorize(Roles = "admin,op")]，
        // 无 Authorization 头时 ApiTokensBearer 方案应 challenge（401）或 forbid（403），绝不应是 200/500。
        HttpResponseMessage response = await _client.PostAsync("/forza/vehicles", content: null);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Betting_admin_bets_without_token_is_challenged()
    {
        // BettingController.GetBets 带 [Authorize(Roles = "admin,op")]，
        // 无凭据应被鉴权管线拦截（401/403），不应触达 SQL。
        HttpResponseMessage response = await _client.GetAsync("/betting/bets");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }
}

/// <summary>
/// Development 环境下的 TestController 冒烟：与上面的 Production 404 测试配对，
/// 共同证明 [DevelopmentOnly] 网关是“可承重的”——在 Development 下端点恢复可用（200）。
/// 通过 WithWebHostBuilder 仅覆盖环境为 Development；工厂的 ConfigureWebHost 仍先执行，
/// 因此后台服务移除 / SQLHelper 替身 / 出站 HTTP 阻断等隔离手段依然生效。
/// </summary>
public class TestControllerDevelopmentSmokeTests : IClassFixture<CandyBotWebApplicationFactory>
{
    private readonly HttpClient _client;

    public TestControllerDevelopmentSmokeTests(CandyBotWebApplicationFactory factory)
    {
        _client = factory
            .WithWebHostBuilder(builder => builder.UseEnvironment(Environments.Development))
            .CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });
    }

    [Fact]
    public async Task Test_getxml_returns_ok_in_development()
    {
        // Development 下 [DevelopmentOnly] 放行：GetRSAXMLString 返回 [公钥, 私钥]。
        HttpResponseMessage response = await _client.GetAsync("/test/getxml");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "TestController 在 Development 下应正常返回 200");
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("<RSAKeyValue>", "应返回 RSA 密钥 XML");
    }

    [Fact]
    public async Task Test_gethmacsha256_returns_ok_in_development()
    {
        // Development 下 [DevelopmentOnly] 放行：HMAC-SHA256 端点恢复可用，返回 64 字符小写十六进制。
        HttpResponseMessage response = await _client.GetAsync("/test/gethmacsha256?msg=a&key=b");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "TestController 在 Development 下应正常返回 200");
        string body = await response.Content.ReadAsStringAsync();
        body.Should().MatchRegex("^[0-9a-f]{64}$", "HMAC-SHA256 输出应为 64 字符小写十六进制字符串");
    }
}
