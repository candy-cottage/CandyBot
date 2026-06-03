using Milimoe.FunGame.Core.Interface.Base;
using Milimoe.FunGame.Core.Model;

namespace CandyCottage.CandyBot.Interfaces
{
    public interface ITaskSchedulerManager
    {
        public IReadOnlyList<IScheduledTask> GetAllTasks();
        public ScheduledTask? GetScheduledTask(string name);
        public RecurringTask? GetRecurringTask(string name);
        public bool PauseTask(string name);
        public bool ResumeTask(string name);
        public void AddTask<T>(T task) where T : IScheduledTask;
        public void AddScheduledTask(ScheduledTask task);
        public void AddRecurringTask(RecurringTask task);
        public bool IsPaused(string name);
    }
}
