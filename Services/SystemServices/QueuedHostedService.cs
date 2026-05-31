using CandyCottage.CandyBot.Interfaces;

namespace CandyCottage.CandyBot.Services.SystemServices;

public class QueuedHostedService(IBackgroundTaskQueue taskQueue, ILogger<QueuedHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Queued Hosted Service is running.");
        // 持续的消费循环，直到应用停止
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 1. 从队列中异步取出一个工作项，若无任务将高效等待
                Func<CancellationToken, Task> workItem = await taskQueue.DequeueAsync(stoppingToken);

                // 2. 执行工作项。务必捕获异常，避免后台服务崩溃
                await workItem(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Shutdown requested, stopping the background service.");
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An error occurred while processing a background task.");
            }
        }
        logger.LogInformation("Queued Hosted Service has stopped.");
    }
}
