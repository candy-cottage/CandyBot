using CandyCottage.CandyBot.Utilities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Milimoe.FunGame.Core.Library.Constant;

namespace CandyBot.IntegrationTests.Infrastructure;

/// <summary>
/// 把 ASP.NET 测试宿主指向<b>真实 MySQL 容器</b>的工厂，专用于鉴权（ApiTokensBearerHandler）
/// 端到端集成测试：与 Phase 5 的 <see cref="CandyBotWebApplicationFactory"/> 不同，
/// 本工厂<b>不替换</b> <see cref="SQLHelper"/>，而是让应用真实地解析到一个绑定到容器的
/// <see cref="MySQLHelper"/>，从而让 <c>ApiTokensBearerHandler</c> 的鉴权 SQL 查询
/// 跑在 <see cref="MySqlFixture"/> 共享 schema 库里被种子的数据上。
///
/// <para>
/// 由于 <see cref="MySqlFixture"/> 是集合夹具（非类夹具），其连接坐标只在运行期才可知，
/// 因此本工厂需在测试类里<b>手动构造</b>（传入坐标），并由测试类负责 Dispose。
/// </para>
///
/// <para>接管 DI 的方式（<see cref="ConfigureWebHost"/> → <c>ConfigureTestServices</c>）：</para>
/// <list type="number">
///   <item>移除全部 <see cref="IHostedService"/>：避免开启真实 WebSocket 网关、写登录日志等。</item>
///   <item>替换 <see cref="IHttpClientFactory"/>：任何出站 HTTP 都抛异常（纵深防御）。</item>
///   <item>覆盖 <see cref="SQLConnectionConfig"/> 单例为容器坐标（MySQL 模式）。应用以
///   <c>AddScoped(SQLConnectionConfig.NewSQLHelper)</c> 注册 <see cref="SQLHelper"/>，
///   它依 <see cref="SQLConnectionConfig.Mode"/> 解析到 <c>AddScoped&lt;MySQLHelper&gt;()</c>，
///   而 <see cref="MySQLHelper"/> 从该 <see cref="SQLConnectionConfig"/> 构造连接串——于是
///   <see cref="SQLHelper"/> 即绑定到容器。</item>
/// </list>
/// </summary>
public sealed class MySqlWebApplicationFactory(
    string host, ushort port, string database, string username, string password)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // 用 Production，避免映射 /scalar 与 OpenAPI（非本测试目标）。
        builder.UseEnvironment(Environments.Production);

        builder.ConfigureTestServices(services =>
        {
            // 1. 移除全部后台服务：QQBotWebSocketService 会打开真实 WebSocket；
            //    SystemInitializedService 会向数据库写入登录日志；等等。
            services.RemoveAll<IHostedService>();

            // 2. 阻断出站 HTTP，确保测试不会触达外部网络。
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new BlockingHttpClientFactory());

            // 3. 把 SQLConnectionConfig 覆盖为容器坐标（MySQL 模式）。
            //    应用通过 SQLConnectionConfig.NewSQLHelper 工厂解析 SQLHelper，
            //    MySQL 模式下取 MySQLHelper（Program.cs 已 AddScoped<MySQLHelper>()），
            //    MySQLHelper 从此 config 构造连接串——SQLHelper 即指向容器。
            services.RemoveAll<SQLConnectionConfig>();
            services.AddSingleton(new SQLConnectionConfig
            {
                Mode = SQLMode.MySQL,
                DataSource = host,
                Port = port.ToString(),
                DataBase = database,
                User = username,
                Password = password,
            });
        });
    }

    /// <summary>返回的 <see cref="HttpClient"/> 在任何请求时都抛异常，确保测试不触达外部网络。</summary>
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
