using System.Net;
using CandyBot.IntegrationTests.Infrastructure;
using FluentAssertions;
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
    public async Task Test_getxml_returns_ok_with_two_keys()
    {
        // TestController.GetRSAXMLString：纯加密，无数据库 / 网络。返回 [公钥, 私钥]。
        HttpResponseMessage response = await _client.GetAsync("/test/getxml");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("<RSAKeyValue>");
    }

    [Fact]
    public async Task Test_gethmacsha256_returns_ok()
    {
        // TestController.UseHMACSHA256：纯加密，匿名可访问。
        HttpResponseMessage response = await _client.GetAsync("/test/gethmacsha256?msg=a&key=b");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Test_gethmacsha512_returns_ok()
    {
        // TestController.UseHMACSHA512：纯加密，匿名可访问。
        HttpResponseMessage response = await _client.GetAsync("/test/gethmacsha512?msg=a&key=b");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().NotBeNullOrWhiteSpace();
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
