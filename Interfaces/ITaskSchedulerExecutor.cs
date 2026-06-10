namespace CandyCottage.CandyBot.Interfaces
{
    public interface ITaskSchedulerExecutor
    {
        public bool RunTaskNow(string taskName, CancellationToken cancellationToken = default);
    }
}
