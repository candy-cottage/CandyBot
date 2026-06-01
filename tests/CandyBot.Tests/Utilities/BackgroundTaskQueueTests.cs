using CandyCottage.CandyBot.Utilities;
using FluentAssertions;
using Xunit;

namespace CandyBot.Tests.Utilities;

/// <summary>
/// B14：后台任务队列的入队/出队、有界容量阻塞与取消语义。
/// </summary>
public class BackgroundTaskQueueTests
{
    private static Func<CancellationToken, Task> Work(string tag, List<string> sink) =>
        _ => { sink.Add(tag); return Task.CompletedTask; };

    [Fact]
    public async Task 出队顺序应与入队顺序一致_FIFO()
    {
        BackgroundTaskQueue queue = new(capacity: 10);
        List<string> executed = [];

        await queue.QueueAsync(Work("a", executed));
        await queue.QueueAsync(Work("b", executed));
        await queue.QueueAsync(Work("c", executed));

        for (int i = 0; i < 3; i++)
        {
            Func<CancellationToken, Task> item = await queue.DequeueAsync(CancellationToken.None);
            await item(CancellationToken.None);
        }

        executed.Should().Equal("a", "b", "c");
    }

    [Fact]
    public async Task 队列已满时入队应阻塞_出队腾出空间后才完成()
    {
        BackgroundTaskQueue queue = new(capacity: 1);
        List<string> sink = [];

        // 填满容量为 1 的队列
        await queue.QueueAsync(Work("first", sink));

        // 再次入队：队列已满，ValueTask 不应立即完成
        ValueTask pending = queue.QueueAsync(Work("second", sink));
        pending.IsCompleted.Should().BeFalse("队列已满，写入应阻塞直到有空间");

        // 出队一个，腾出空间
        Func<CancellationToken, Task> first = await queue.DequeueAsync(CancellationToken.None);

        // 现在挂起的入队应能完成
        await pending.AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        pending.IsCompleted.Should().BeTrue();

        // 验证两个任务都最终在队列中可被取出并按序执行
        await first(CancellationToken.None);
        Func<CancellationToken, Task> second = await queue.DequeueAsync(CancellationToken.None);
        await second(CancellationToken.None);
        sink.Should().Equal("first", "second");
    }

    [Fact]
    public async Task 空队列上以已取消的令牌出队应抛出OperationCanceledException()
    {
        BackgroundTaskQueue queue = new(capacity: 1);
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        Func<Task> act = async () => await queue.DequeueAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task 入队空任务应抛出ArgumentNullException()
    {
        BackgroundTaskQueue queue = new(capacity: 1);

        Func<Task> act = async () => await queue.QueueAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
