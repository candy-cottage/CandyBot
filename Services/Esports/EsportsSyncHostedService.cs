using CandyCottage.CandyBot.Utilities;

namespace CandyCottage.CandyBot.Services.Esports;

public sealed class EsportsSyncHostedService(
    IServiceScopeFactory scopeFactory,
    CandyBotFunctionConfig functionConfig,
    PandaScoreConfig pandaScoreConfig,
    ILogger<EsportsSyncHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTime nextSchedule = DateTime.MinValue;
        DateTime nextResult = DateTime.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (functionConfig.BettingAutomation)
                {
                    DateTime now = DateTime.UtcNow;
                    using IServiceScope scope = scopeFactory.CreateScope();
                    EsportsSyncService sync = scope.ServiceProvider.GetRequiredService<EsportsSyncService>();

                    if (now >= nextSchedule)
                    {
                        await sync.SyncSchedulesAsync(stoppingToken);
                        nextSchedule = now.Add(pandaScoreConfig.SchedulePollInterval);
                    }
                    if (now >= nextResult)
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
