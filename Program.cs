using CandyCottage.CandyBot.Interfaces;
using CandyCottage.CandyBot.Modules;
using CandyCottage.CandyBot.Services;
using CandyCottage.CandyBot.Services.BotServices;
using CandyCottage.CandyBot.Services.SystemServices;
using CandyCottage.CandyBot.Utilities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging.Console;
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
builder.Services.AddScoped(SQLConnectionConfig.NewSQLHelper);
builder.Services.AddScoped<SQLTableInitializeService>();
builder.Services.AddHostedService<QQBotWebSocketService>();
builder.Services.AddScoped<QQBotService>();
builder.Services.AddScoped<IBotReplySender>(sp => sp.GetRequiredService<QQBotService>());
builder.Services.AddScoped<SheetService>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<BettingService>();
builder.Services.AddScoped<ForzaService>();
builder.Services.AddScoped<NoOpSheetParseDiagnostic>();
builder.Services.AddScoped<DeepSeekSheetParseDiagnostic>();
builder.Services.AddScoped<ISheetParseDiagnostic>(sp =>
{
    DeepSeekConfig config = sp.GetRequiredService<DeepSeekConfig>();
    return config.Enabled && !string.IsNullOrWhiteSpace(config.ApiKey)
        ? sp.GetRequiredService<DeepSeekSheetParseDiagnostic>()
        : sp.GetRequiredService<NoOpSheetParseDiagnostic>();
});
builder.Services.AddScoped<IForzaSheetImporter, ForzaSheetImporter>();
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
