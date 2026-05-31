namespace CandyCottage.CandyBot.Interfaces;

public interface IBackgroundTaskQueue
{
    public ValueTask QueueAsync(Func<CancellationToken, Task> workItem);
    public ValueTask<Func<CancellationToken, Task>> DequeueAsync(CancellationToken cancellationToken);
}
