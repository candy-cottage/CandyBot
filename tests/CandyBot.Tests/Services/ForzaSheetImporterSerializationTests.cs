using CandyCottage.CandyBot.Services;
using FluentAssertions;
using Xunit;

namespace CandyBot.Tests.Services;

/// <summary>
/// 单元测试：ForzaSheetImporter 的按来源串行化辅助方法 <see cref="ForzaSheetImporter.RunSerializedAsync{T}"/>。
/// 不依赖数据库，完全确定性（无 Task.Delay）。
/// </summary>
public class ForzaSheetImporterSerializationTests
{
    // =========================================================================
    // 1. 同一 fileId 并发两次 → 最大并发数 == 1
    // =========================================================================
    [Fact]
    public async Task 同一fileId_并发两次_被串行化()
    {
        int maxConcurrency = 0;
        int currentConcurrency = 0;
        // TCS 用于让两个并发任务的"第一次进入"都到达后再各自继续，
        // 目的是放大并发窗口，使任何锁失效都能被检测到。
        TaskCompletionSource<bool> gate = new();

        async Task<int> SlowBody()
        {
            int c = Interlocked.Increment(ref currentConcurrency);
            // 原子记录最大并发值
            int prev;
            do { prev = maxConcurrency; } while (prev < c && Interlocked.CompareExchange(ref maxConcurrency, c, prev) != prev);

            // 第一个进入者打开门；第二个进入者等门（但因锁串行，这一行实际只会被一个人等）
            if (!gate.TrySetResult(true))
                await gate.Task;

            await Task.Yield(); // 让出执行权，给其他任务机会在锁之外推进
            Interlocked.Decrement(ref currentConcurrency);
            return 0;
        }

        const string fileId = "same-file";
        Task<int> t1 = ForzaSheetImporter.RunSerializedAsync(fileId, SlowBody, CancellationToken.None);
        Task<int> t2 = ForzaSheetImporter.RunSerializedAsync(fileId, SlowBody, CancellationToken.None);

        await Task.WhenAll(t1, t2);

        maxConcurrency.Should().Be(1, "同一 fileId 的两次同步应被串行化，最大并发为 1");
    }

    // =========================================================================
    // 2. 不同 fileId 并发两次 → 可以同时执行（最大并发数 == 2）
    // =========================================================================
    [Fact]
    public async Task 不同fileId_并发两次_可以并发()
    {
        int maxConcurrency = 0;
        int currentConcurrency = 0;
        // 用两个 TCS 使两个任务在"都进入临界区"时互相等待，放大并发窗口
        TaskCompletionSource<bool> entered1 = new();
        TaskCompletionSource<bool> entered2 = new();

        async Task<int> Body(string id)
        {
            int c = Interlocked.Increment(ref currentConcurrency);
            int prev;
            do { prev = maxConcurrency; } while (prev < c && Interlocked.CompareExchange(ref maxConcurrency, c, prev) != prev);

            // 双方各自标记"已进入"，然后等对方也进入
            if (id == "file-A") { entered1.TrySetResult(true); await entered2.Task; }
            else                { entered2.TrySetResult(true); await entered1.Task; }

            Interlocked.Decrement(ref currentConcurrency);
            return 0;
        }

        Task<int> t1 = ForzaSheetImporter.RunSerializedAsync("file-A", () => Body("file-A"), CancellationToken.None);
        Task<int> t2 = ForzaSheetImporter.RunSerializedAsync("file-B", () => Body("file-B"), CancellationToken.None);

        await Task.WhenAll(t1, t2);

        maxConcurrency.Should().Be(2, "不同 fileId 应允许并发，最大并发为 2");
    }
}
