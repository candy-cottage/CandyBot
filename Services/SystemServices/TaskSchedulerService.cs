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
                            // 防止同一任务并发执行（任务内部可能耗时超过1秒）
                            if (IsTaskRunning(task.Name)) continue;

                            // 为每个任务启动独立的后台执行
                            Task execTask = Task.Run(() => ExecuteTaskAsync(task, now, stoppingToken), stoppingToken);
                            TrackRunningTask(execTask, task.Name);
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

        private bool IsTaskRunning(string taskName)
        {
            return _runningTaskMap.ContainsKey(taskName);
        }

        private void TrackRunningTask(Task task, string taskName)
        {
            _runningTaskMap[taskName] = task;
            task.ContinueWith(_ =>
            {
                _runningTaskMap.TryRemove(taskName, out Task? _);
            }, TaskContinuationOptions.ExecuteSynchronously);
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
            // 1. 获取任务
            IScheduledTask? task = manager.GetAllTasks().FirstOrDefault(t => t.Name.Equals(taskName, StringComparison.OrdinalIgnoreCase));
            if (task is null) return false;

            // 2. 检查是否正在运行
            if (IsTaskRunning(task.Name))
            {
                logger.LogWarning("Task {TaskName} is already running, cannot execute now.", task.Name);
                return false;
            }

            // 3. 异步执行
            DateTime now = DateTime.Now;
            Task execTask = Task.Run(() => ExecuteTaskAsync(task, now, cancellationToken), cancellationToken).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    logger.LogError(t.Exception, "Manual execution of task {TaskName} failed.", task.Name);
                }
            }, cancellationToken);
            TrackRunningTask(execTask, task.Name);

            return true;
        }
    }
}
