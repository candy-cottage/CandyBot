using CandyCottage.CandyBot.Utilities;

namespace CandyCottage.CandyBot.Services.Esports;

public sealed class EsportsSyncHostedService(
    IServiceScopeFactory scopeFactory,
    CandyBotFunctionConfig functionConfig,
    PandaScoreConfig pandaScoreConfig,
    ILogger<EsportsSyncHostedService> logger) : BackgroundService
{
    /// <summary>
    /// 单 tick 的纯调度决策：自动化关闭时两项均不跑；开启时各自按 <c>now &gt;= next*</c> 的节律独立判定。
    /// 抽成无副作用的静态方法以便单测覆盖节律门控，与 <see cref="ExecuteAsync"/> 的行为保持一致。
    /// </summary>
    internal static (bool RunSchedule, bool RunResult) DecideSyncs(bool automationEnabled, DateTime now, DateTime nextSchedule, DateTime nextResult)
        => automationEnabled ? (now >= nextSchedule, now >= nextResult) : (false, false);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTime nextSchedule = DateTime.MinValue;
        DateTime nextResult = DateTime.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                DateTime now = DateTime.UtcNow;
                (bool runSchedule, bool runResult) = DecideSyncs(functionConfig.BettingAutomation, now, nextSchedule, nextResult);
                if (runSchedule || runResult)
                {
                    using IServiceScope scope = scopeFactory.CreateScope();
                    EsportsSyncService sync = scope.ServiceProvider.GetRequiredService<EsportsSyncService>();

                    if (runSchedule)
                    {
                        await sync.SyncSchedulesAsync(stoppingToken);
                        nextSchedule = now.Add(pandaScoreConfig.SchedulePollInterval);
                    }
                    if (runResult)
                    {
                        await sync.SyncResultsAsync(stoppingToken);
                        nextResult = now.Add(pandaScoreConfig.ResultPollInterval);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "赛事自动化同步轮次失败");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
