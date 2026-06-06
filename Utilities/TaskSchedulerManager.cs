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

        public void AddScheduledTask(ScheduledTask task, bool runToday = false) => AddTask(task, false, runToday);

        public void AddRecurringTask(RecurringTask task, bool runNow = false) => AddTask(task, runNow);

        public bool IsPaused(string name) => _pausedTasks.ContainsKey(name);

        private void AddTask<T>(T task, bool runNow = false, bool runToday = false) where T : IScheduledTask
        {
            // 如果任务已存在，可选择覆盖或抛出异常，这里采用覆盖策略
            _tasks[task.Name] = task;
            // 确保新添加的任务没有被暂停
            _pausedTasks.TryRemove(task.Name, out _);

            // 根据是否 runNow 初始化 NextRun
            if (task is RecurringTask rt && rt.NextRun == DateTime.MaxValue)
            {
                rt.NextRun = runNow ? DateTime.Now : DateTime.Now + rt.Interval;
            }

            // 对于时间计划型，如果 runToday，设置为昨天以便系统可以正确识别；反之，检查当前时间，没到计划时间时不赋值
            // 时间计划必须以执行时间为基准，每次执行都必须是确定的时间点，与添加时机无关
            if (task is ScheduledTask st)
            {
                if (DateTime.Now > DateTime.Today.Add(st.TimeOfDay))
                {
                    st.LastRun = DateTime.Today.Add(st.TimeOfDay);
                }
                else if (runToday)
                {
                    st.LastRun = DateTime.Today.AddDays(-1).Add(st.TimeOfDay);
                }
            }
        }
    }
}
