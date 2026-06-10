using CandyCottage.CandyBot.Interfaces;
using CandyCottage.CandyBot.Services.SystemServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Milimoe.FunGame.Core.Interface.Base;
using Milimoe.FunGame.Core.Model;
using NSubstitute;
using Xunit;

namespace CandyBot.Tests.Services;

/// <summary>
/// RunTaskNow（立即执行任务计划）的边界与并发行为：
/// 占位必须是原子的——定时循环与手动触发并发到达时，同一任务只允许一次执行。
/// </summary>
public class TaskSchedulerServiceTests
{
    private static TaskSchedulerService CreateService(params ScheduledTask[] tasks)
    {
        ITaskSchedulerManager manager = Substitute.For<ITaskSchedulerManager>();
        manager.GetAllTasks().Returns([.. tasks.Cast<IScheduledTask>()]);
        return new TaskSchedulerService(NullLogger<TaskSchedulerService>.Instance, manager);
    }

    [Fact]
    public void RunTaskNow_未知任务名_返回false()
    {
        TaskSchedulerService svc = CreateService();

        svc.RunTaskNow("不存在的任务").Should().BeFalse();
    }

    [Fact]
    public void RunTaskNow_任务名忽略大小写匹配()
    {
        ScheduledTask task = new("SyncCars", TimeSpan.Zero, () => { });
        TaskSchedulerService svc = CreateService(task);

        svc.RunTaskNow("synccars").Should().BeTrue();
    }

    [Fact]
    public void RunTaskNow_任务运行中再次触发_返回false()
    {
        using ManualResetEventSlim started = new();
        using ManualResetEventSlim release = new();
        ScheduledTask task = new("同步任务", TimeSpan.Zero, () => { started.Set(); release.Wait(); });
        TaskSchedulerService svc = CreateService(task);

        try
        {
            svc.RunTaskNow("同步任务").Should().BeTrue();
            started.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("任务应已开始执行");
            svc.RunTaskNow("同步任务").Should().BeFalse("任务运行期间不允许重复触发");
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void RunTaskNow_并发触发同一任务_只有一个成功()
    {
        using ManualResetEventSlim release = new();
        ScheduledTask task = new("同步任务", TimeSpan.Zero, release.Wait);
        TaskSchedulerService svc = CreateService(task);

        bool[] results = new bool[8];
        try
        {
            Parallel.For(0, results.Length, i => results[i] = svc.RunTaskNow("同步任务"));
        }
        finally
        {
            release.Set();
        }

        results.Count(r => r).Should().Be(1, "占位必须是原子的，任意时刻同一任务只能有一次执行");
    }
}
