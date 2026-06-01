using CandyCottage.CandyBot.Utilities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Milimoe.FunGame.Core.Api.Transmittal;
using NSubstitute;

namespace CandyBot.IntegrationTests.Infrastructure;

/// <summary>
/// 无凭据的测试宿主：在不连接 QQ 网关、腾讯文档 API 或生产数据库的前提下，
/// 让 MVC 路由 / 控制器激活可在 CI 中被验证。
/// 隔离手段：
///   1. 移除所有 <see cref="IHostedService"/>（WebSocket 网关、登录日志写库、任务队列等）。
///   2. 用 NSubstitute 替换 <see cref="SQLHelper"/>，使依赖它的控制器无需真实数据库即可构造。
///   3. 替换 <see cref="IHttpClientFactory"/>，任何出站 HTTP 都会抛异常（纵深防御）。
/// </summary>
public class CandyBotWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // 默认用 Production，避免无意中映射 /scalar 与 OpenAPI（这些非冒烟测试目标）。
        builder.UseEnvironment(Environments.Production);

        builder.ConfigureTestServices(services =>
        {
            // 1. 移除全部后台服务：QQBotWebSocketService 会打开真实 WebSocket；
            //    SystemInitializedService 会向数据库写入服务器登录日志；等等。
            services.RemoveAll<IHostedService>();

            // 2. 用替身替换 SQLHelper，控制器（TestController/ForzaService/BettingService）
            //    才能在没有真实数据库的情况下被激活。冒烟端点本身不执行任何 SQL。
            //    同时移除具体的 MySQLHelper / SQLiteHelper 注册，防止测试意外打开
            //    真实的 candybot.db 文件（它们在 Program.cs 中作为 scoped 注册，
            //    是为 SQLTableInitializeService 的 is 类型判断而存在的）。
            services.RemoveAll<SQLHelper>();
            services.RemoveAll<MySQLHelper>();
            services.RemoveAll<SQLiteHelper>();
            services.AddScoped(_ => Substitute.For<SQLHelper>());

            // 3. 替换 IHttpClientFactory，保证测试期间没有任何出站 HTTP 能离开进程。
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new BlockingHttpClientFactory());
        });
    }

    /// <summary>
    /// 返回的 <see cref="HttpClient"/> 在任何请求时都抛异常，确保测试不会触达外部网络。
    /// </summary>
    private sealed class BlockingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new BlockingHttpMessageHandler());
    }

    private sealed class BlockingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("测试期间已禁止出站 HTTP（outbound HTTP blocked in tests）。");
    }
}
