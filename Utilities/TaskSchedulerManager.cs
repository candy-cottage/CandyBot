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

        public void AddScheduledTask(ScheduledTask task, bool todayMustRun = false) => AddTask(task, false, todayMustRun);

        public void AddRecurringTask(RecurringTask task, bool runNow = false) => AddTask(task, runNow);

        public bool IsPaused(string name) => _pausedTasks.ContainsKey(name);

        private void AddTask<T>(T task, bool runNow = false, bool todayMustRun = false) where T : IScheduledTask
        {
            // 如果任务已存在，可选择覆盖或抛出异常，这里采用覆盖策略
            _tasks[task.Name] = task;
            // 确保新添加的任务没有被暂停
            _pausedTasks.TryRemove(task.Name, out _);

            DateTime now = DateTime.Now;

            // 根据是否 runNow 初始化 NextRun
            if (task is RecurringTask rt && rt.NextRun == DateTime.MaxValue)
            {
                rt.NextRun = runNow ? now : now + rt.Interval;
            }

            // 每日时间计划必须以设定时间为基准，每次执行都必须是确定的时间点，与添加时机无关
            // todayMustRun 用于在添加时间大于设定时间时，强制将 LastRun 设置为昨天的同一时间，以确保当天会执行
            if (task is ScheduledTask st && now > DateTime.Today.Add(st.TimeOfDay))
            {
                st.LastRun = todayMustRun ? DateTime.Today.AddDays(-1).Add(st.TimeOfDay) : DateTime.Today.Add(st.TimeOfDay);
            }
        }
    }
}
