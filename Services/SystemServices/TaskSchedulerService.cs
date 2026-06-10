using System.Collections.Concurrent;
using CandyCottage.CandyBot.Interfaces;
using Milimoe.FunGame.Core.Interface.Base;
using Milimoe.FunGame.Core.Model;

namespace CandyCottage.CandyBot.Services.SystemServices
{
    public class TaskSchedulerService(ILogger<TaskSchedulerService> logger, ITaskSchedulerManager manager) : BackgroundService, ITaskSchedulerExecutor
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation("Task Scheduler Service is running.");
            using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    while (await timer.WaitForNextTickAsync(stoppingToken))
                    {
                        DateTime now = DateTime.Now;
                        List<IScheduledTask>? tasksToRun = [.. manager.GetAllTasks().Where(t => !manager.IsPaused(t.Name) && t.NextRun <= now)];

                        foreach (IScheduledTask task in tasksToRun)
                        {
                            // 防止同一任务并发执行（任务内部可能耗时超过1秒，或与手动触发竞争）
                            TryStartTask(task, now, stoppingToken);
                        }
                    }
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

            await WaitForRunningTasksAsync(TimeSpan.FromSeconds(5));
            logger.LogInformation("Task Scheduler Service has stopped.");
        }

        private async Task ExecuteTaskAsync(IScheduledTask task, DateTime scheduledStartTime, CancellationToken cancellationToken)
        {
            Exception? error = null;

            try
            {
                // 执行任务逻辑（支持取消）
                await Task.Run(() => task.Action(), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 任务被取消，不视为错误
                logger.LogWarning("Task {TaskName} was canceled.", task.Name);
                return;
            }
            catch (Exception ex)
            {
                error = ex;
                logger.LogError(ex, "Task {TaskName} failed.", task.Name);
                task.ErrorHandler?.Invoke(ex);
            }
            finally
            {
                DateTime endTime = DateTime.Now;

                // 更新下次执行时间
                if (task is ScheduledTask st)
                {
                    st.ExecutedTimeSpan = endTime - scheduledStartTime;
                    st.LastRun = scheduledStartTime;
                    st.Error = error;
                    // ScheduledTask 的 NextRun 是只读属性，依赖 LastRun 的变化自动计算
                }
                else if (task is RecurringTask rt)
                {
                    rt.ExecutedTimeSpan = endTime - scheduledStartTime;
                    rt.LastRun = scheduledStartTime;
                    rt.Error = error;
                    // 基于执行开始时间计算下一次执行时间（防止执行耗时过长导致的偏移）
                    rt.NextRun = scheduledStartTime + rt.Interval;
                }

                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug("Task {TaskName} completed in {ElapsedMs} ms. Next run at {NextRun}", task.Name, task.ExecutedTimeSpan.TotalMilliseconds, task.NextRun);
                }
            }
        }

        private readonly ConcurrentDictionary<string, Task> _runningTaskMap = new();

        /// <summary>
        /// 原子地把任务标记为运行中并启动后台执行；任务已在运行时返回 false。
        /// 占位（TryAdd）必须发生在启动之前：定时循环与手动触发（或两次手动触发）可能并发到达，
        /// 先检查后登记会让双方都通过“未在运行”检查，导致同一任务重复执行。
        /// </summary>
        private bool TryStartTask(IScheduledTask task, DateTime scheduledStartTime, CancellationToken cancellationToken)
        {
            TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_runningTaskMap.TryAdd(task.Name, completion.Task)) return false;

            Task.Run(() => ExecuteTaskAsync(task, scheduledStartTime, cancellationToken), cancellationToken)
                .ContinueWith(t =>
                {
                    _runningTaskMap.TryRemove(task.Name, out Task? _);
                    completion.SetResult();
                    if (t.IsFaulted)
                    {
                        logger.LogError(t.Exception, "Task {TaskName} terminated unexpectedly.", task.Name);
                    }
                }, TaskContinuationOptions.ExecuteSynchronously);
            return true;
        }

        private async Task WaitForRunningTasksAsync(TimeSpan timeout)
        {
            if (_runningTaskMap.IsEmpty) return;

            if (logger.IsEnabled(LogLevel.Information)) logger.LogInformation("Waiting for {Count} running tasks to complete...", _runningTaskMap.Count);
            Task[] allTasks = [.. _runningTaskMap.Values];
            Task waitTask = Task.WhenAll(allTasks);
            if (await Task.WhenAny(waitTask, Task.Delay(timeout)) == waitTask)
            {
                logger.LogInformation("All running tasks completed.");
            }
            else
            {
                logger.LogWarning("Timeout while waiting for tasks to complete.");
            }
        }

        public bool RunTaskNow(string taskName, CancellationToken cancellationToken = default)
        {
            IScheduledTask? task = manager.GetAllTasks().FirstOrDefault(t => t.Name.Equals(taskName, StringComparison.OrdinalIgnoreCase));
            if (task is null) return false;

            if (!TryStartTask(task, DateTime.Now, cancellationToken))
            {
                logger.LogWarning("Task {TaskName} is already running, cannot execute now.", task.Name);
                return false;
            }
            return true;
        }
    }
}
