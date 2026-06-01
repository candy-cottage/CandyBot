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
    public async Task Test_getxml_returns_ok_with_two_keys()
    {
        // TestController.GetRSAXMLString：纯加密，无数据库 / 网络。返回 [公钥, 私钥]。
        // 端点序列化 string[2]，两个元素均包含 <RSAKeyValue> 标签（index 0 公钥、index 1 私钥）。
        HttpResponseMessage response = await _client.GetAsync("/test/getxml");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        // 精确断言：JSON 数组中恰好包含两个 <RSAKeyValue> 片段（公钥 + 私钥各一个）。
        int keyCount = 0;
        int idx = 0;
        while ((idx = body.IndexOf("<RSAKeyValue>", idx, StringComparison.Ordinal)) >= 0)
        {
            keyCount++;
            idx++;
        }
        keyCount.Should().Be(2, "端点应返回恰好两个 RSA 密钥（公钥 + 私钥）");
    }

    [Fact]
    public async Task Test_gethmacsha256_returns_ok()
    {
        // TestController.UseHMACSHA256：调用 Encryption.HmacSha256(msg, key)，纯加密，匿名可访问。
        // 实测：固定输入 msg="a", key="b" → 64 字符小写十六进制字符串，具有确定性。
        // Ok(string) 将字符串直接写入响应体（裸字符串，无 JSON 引号）。
        HttpResponseMessage response1 = await _client.GetAsync("/test/gethmacsha256?msg=a&key=b");
        HttpResponseMessage response2 = await _client.GetAsync("/test/gethmacsha256?msg=a&key=b");

        response1.StatusCode.Should().Be(HttpStatusCode.OK);
        string body1 = await response1.Content.ReadAsStringAsync();
        string body2 = await response2.Content.ReadAsStringAsync();

        // 实测：Ok(string) 直接将字符串写入响应体（非 JSON 引号包裹），
        // 即响应体 = 裸哈希字符串（64 字符小写十六进制）。
        body1.Should().HaveLength(64, "HMAC-SHA256 输出应为 64 个十六进制字符");
        body1.Should().MatchRegex("^[0-9a-f]{64}$", "HMAC-SHA256 输出应为小写十六进制字符串");
        // 确定性：相同输入两次调用结果一致。
        body1.Should().Be(body2, "相同输入应产生相同的 HMAC-SHA256 值（确定性函数）");
    }

    [Fact]
    public async Task Test_gethmacsha512_returns_ok()
    {
        // TestController.UseHMACSHA512：调用 msg.Encrypt(key)（StringExtension.Encrypt = HMAC-SHA512），
        // 纯加密，匿名可访问。
        // 实测：固定输入 msg="a", key="b" → 128 字符小写十六进制字符串，具有确定性。
        HttpResponseMessage response1 = await _client.GetAsync("/test/gethmacsha512?msg=a&key=b");
        HttpResponseMessage response2 = await _client.GetAsync("/test/gethmacsha512?msg=a&key=b");

        response1.StatusCode.Should().Be(HttpStatusCode.OK);
        string body1 = await response1.Content.ReadAsStringAsync();
        string body2 = await response2.Content.ReadAsStringAsync();

        // 实测：Ok(string) 直接将字符串写入响应体（非 JSON 引号包裹），
        // 即响应体 = 裸哈希字符串（128 字符小写十六进制）。
        body1.Should().HaveLength(128, "HMAC-SHA512 输出应为 128 个十六进制字符");
        body1.Should().MatchRegex("^[0-9a-f]{128}$", "HMAC-SHA512 输出应为小写十六进制字符串");
        // 确定性：相同输入两次调用结果一致。
        body1.Should().Be(body2, "相同输入应产生相同的 HMAC-SHA512 值（确定性函数）");
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
