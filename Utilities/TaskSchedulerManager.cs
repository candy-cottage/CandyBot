using System.Collections.Concurrent;
using CandyCottage.CandyBot.Interfaces;
using Milimoe.FunGame.Core.Interface.Base;
using Milimoe.FunGame.Core.Model;

namespace CandyCottage.CandyBot.Utilities
{
    public class TaskSchedulerManager : ITaskSchedulerManager
    {
        private readonly ConcurrentDictionary<string, IScheduledTask> _tasks = new();
        private readonly ConcurrentDictionary<string, bool> _pausedTasks = new();

        public IReadOnlyList<IScheduledTask> GetAllTasks() => [.. _tasks.Values];

        public ScheduledTask? GetScheduledTask(string name)
        {
            if (_tasks.TryGetValue(name, out IScheduledTask? task) && task is ScheduledTask scheduled)
                return scheduled;
            return null;
        }

        public RecurringTask? GetRecurringTask(string name)
        {
            if (_tasks.TryGetValue(name, out IScheduledTask? task) && task is RecurringTask recurring)
                return recurring;
            return null;
        }

        public bool PauseTask(string name)
        {
            if (!_tasks.ContainsKey(name)) return false;
            _pausedTasks[name] = true;
            return true;
        }

        public bool ResumeTask(string name)
        {
            if (!_tasks.ContainsKey(name)) return false;
            _pausedTasks.TryRemove(name, out _);
            return true;
        }

        public void AddTask<T>(T task) where T : IScheduledTask
        {
            // 如果任务已存在，可选择覆盖或抛出异常，这里采用覆盖策略
            _tasks[task.Name] = task;
            // 确保新添加的任务没有被暂停
            _pausedTasks.TryRemove(task.Name, out _);

            // 如果是 RecurringTask 且未初始化 NextRun，则设定为当前时间 + 间隔
            if (task is RecurringTask rt && rt.NextRun == DateTime.MaxValue)
            {
                rt.NextRun = DateTime.Now + rt.Interval;
            }
        }

        public void AddScheduledTask(ScheduledTask task) => AddTask(task);

        public void AddRecurringTask(RecurringTask task) => AddTask(task);

        public bool IsPaused(string name) => _pausedTasks.ContainsKey(name);
    }
}
