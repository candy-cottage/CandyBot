using CandyCottage.CandyBot.Interfaces;
using CandyCottage.CandyBot.Modules;
using CandyCottage.CandyBot.Services;
using CandyCottage.CandyBot.Services.BotServices;
using CandyCottage.CandyBot.Services.Esports;
using CandyCottage.CandyBot.Services.SystemServices;
using CandyCottage.CandyBot.Utilities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging.Console;
using Milimoe.FunGame.Core.Api.Transmittal;
using Milimoe.FunGame.Core.Library.Constant;
using Scalar.AspNetCore;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.FormatterName = ConsoleFormatterNames.Simple;
});
builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
    options.IncludeScopes = true;
});
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAnyOrigin", policy =>
    {
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    });
});
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = "ApiTokensBearer";
    options.DefaultChallengeScheme = "ApiTokensBearer";
}).AddScheme<AuthenticationSchemeOptions, ApiTokensBearerHandler>("ApiTokensBearer", null);
builder.Services.AddControllersWithViews();
builder.Services.AddOpenApi();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<CandyBotConfig>();
builder.Services.AddSingleton<BotConfig>();
builder.Services.AddSingleton<SQLConnectionConfig>();
builder.Services.AddSingleton<CandyBotFunctionConfig>();
builder.Services.AddSingleton<TencentDocsConfig>();
builder.Services.AddSingleton<DeepSeekConfig>();
builder.Services.AddSingleton<ForzaConfig>();
builder.Services.AddSingleton<PandaScoreConfig>();
builder.Services.AddSingleton<CharacterModule>();
builder.Services.AddSingleton<ItemModule>();
builder.Services.AddSingleton<SkillModule>();
builder.Services.AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();
builder.Services.AddHostedService<QueuedHostedService>();
builder.Services.AddSingleton<ITaskSchedulerManager, TaskSchedulerManager>();
builder.Services.AddHostedService<TaskSchedulerService>();
builder.Services.AddHostedService<SystemInitializeService>();
builder.Services.AddScoped<MySQLHelper>();
builder.Services.AddScoped<SQLiteHelper>();
builder.Services.AddScoped<SQLHelper>(sp =>
{
    SQLConnectionConfig config = sp.GetRequiredService<SQLConnectionConfig>();
    return config.Mode switch
    {
        SQLMode.MySQL => sp.GetRequiredService<MySQLHelper>(),
        SQLMode.SQLite => sp.GetRequiredService<SQLiteHelper>(),
        _ => throw new NotSupportedException($"不支持的数据库模式：{config.Mode}")
    };
});
builder.Services.AddScoped<SQLTableInitializeService>();
builder.Services.AddHostedService<QQBotWebSocketService>();
builder.Services.AddScoped<QQBotService>();
builder.Services.AddScoped<IBotReplySender>(sp => sp.GetRequiredService<QQBotService>());
builder.Services.AddScoped<SheetService>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<UserAPITokenService>();
builder.Services.AddScoped<BettingService>();
builder.Services.AddScoped<ForzaService>();
builder.Services.AddScoped<NoOpSheetParseDiagnostic>();
builder.Services.AddScoped<DeepSeekSheetParseDiagnostic>();
builder.Services.AddScoped<ISheetParseDiagnostic>(sp =>
{
    DeepSeekConfig config = sp.GetRequiredService<DeepSeekConfig>();
    return config.Enabled && !string.IsNullOrWhiteSpace(config.ApiKey) ?
        sp.GetRequiredService<DeepSeekSheetParseDiagnostic>() : sp.GetRequiredService<NoOpSheetParseDiagnostic>();
});
builder.Services.AddScoped<IForzaSheetImporter, ForzaSheetImporter>();
// 赛事自动化：PandaScoreProvider 的构造函数需要一个裸 HttpClient。
// 用 typed client 注册既注入配置好的 HttpClient，又把它绑定为 IEsportsDataProvider 实现，
// 避免 AddScoped<IEsportsDataProvider, PandaScoreProvider>() 在启动时因无法解析 HttpClient 而抛异常。
builder.Services.AddScoped<EsportsSyncService>();
builder.Services.AddHttpClient<IEsportsDataProvider, PandaScoreProvider>();
builder.Services.AddHostedService<EsportsSyncHostedService>();
builder.Services.AddScoped<CandyBotService>();
builder.Services.AddHostedService<SystemInitializedService>();
builder.Services.AddHostedService<ConsoleInputService>();

WebApplication app = builder.Build();

app.UseCors("AllowAnyOrigin");
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
