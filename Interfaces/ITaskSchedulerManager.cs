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
        public void AddScheduledTask(ScheduledTask task, bool todayMustRun = false);
        public void AddRecurringTask(RecurringTask task, bool runNow = false);
        public bool IsPaused(string name);
    }
}
