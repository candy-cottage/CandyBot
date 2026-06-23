using CandyCottage.CandyBot.Utilities;
using FluentAssertions;
using Xunit;

namespace CandyBot.Tests.Services;

/// <summary>
/// 完整快照判据 (D2 保守版) 的 TDD 测试。
/// IsCompleteSnapshot 仅当全部班级 sheet 被解析、无跳过、无空、诊断无失败时才返回 true。
/// </summary>
public class ForzaSyncDecisionTests
{
    [Fact]
    public void 全部班级sheet解析无跳过无空无诊断失败返回true()
    {
        var parsedClassSheets = new[] { "S1", "S2", "A" };
        var skippedClassSheets = Array.Empty<string>();
        var emptyClassSheets = Array.Empty<string>();
        const int diagnosticFailureCount = 0;

        var result = ForzaTuneIdentity.IsCompleteSnapshot(
            parsedClassSheets,
            skippedClassSheets,
            emptyClassSheets,
            diagnosticFailureCount);

        result.Should().BeTrue();
    }

    [Fact]
    public void 有一张sheet进skippedClassSheets返回false()
    {
        var parsedClassSheets = new[] { "S1", "S2" };
        var skippedClassSheets = new[] { "A" };
        var emptyClassSheets = Array.Empty<string>();
        const int diagnosticFailureCount = 0;

        var result = ForzaTuneIdentity.IsCompleteSnapshot(
            parsedClassSheets,
            skippedClassSheets,
            emptyClassSheets,
            diagnosticFailureCount);

        result.Should().BeFalse();
    }

    [Fact]
    public void 有一张sheet进emptyClassSheets返回false()
    {
        var parsedClassSheets = new[] { "S1", "A" };
        var skippedClassSheets = Array.Empty<string>();
        var emptyClassSheets = new[] { "S2" };
        const int diagnosticFailureCount = 0;

        var result = ForzaTuneIdentity.IsCompleteSnapshot(
            parsedClassSheets,
            skippedClassSheets,
            emptyClassSheets,
            diagnosticFailureCount);

        result.Should().BeFalse();
    }

    [Fact]
    public void diagnosticFailureCount大于0返回false()
    {
        var parsedClassSheets = new[] { "S1", "S2", "A" };
        var skippedClassSheets = Array.Empty<string>();
        var emptyClassSheets = Array.Empty<string>();
        const int diagnosticFailureCount = 1;

        var result = ForzaTuneIdentity.IsCompleteSnapshot(
            parsedClassSheets,
            skippedClassSheets,
            emptyClassSheets,
            diagnosticFailureCount);

        result.Should().BeFalse();
    }

    [Fact]
    public void 多个诊断失败也返回false()
    {
        var parsedClassSheets = new[] { "S1", "S2", "A" };
        var skippedClassSheets = Array.Empty<string>();
        var emptyClassSheets = Array.Empty<string>();
        const int diagnosticFailureCount = 5;

        var result = ForzaTuneIdentity.IsCompleteSnapshot(
            parsedClassSheets,
            skippedClassSheets,
            emptyClassSheets,
            diagnosticFailureCount);

        result.Should().BeFalse();
    }

    [Fact]
    public void 同时有跳过和空sheet返回false()
    {
        var parsedClassSheets = new[] { "S1" };
        var skippedClassSheets = new[] { "S2" };
        var emptyClassSheets = new[] { "A" };
        const int diagnosticFailureCount = 0;

        var result = ForzaTuneIdentity.IsCompleteSnapshot(
            parsedClassSheets,
            skippedClassSheets,
            emptyClassSheets,
            diagnosticFailureCount);

        result.Should().BeFalse();
    }

    [Fact]
    public void 空输入集合返回true()
    {
        var parsedClassSheets = Array.Empty<string>();
        var skippedClassSheets = Array.Empty<string>();
        var emptyClassSheets = Array.Empty<string>();
        const int diagnosticFailureCount = 0;

        var result = ForzaTuneIdentity.IsCompleteSnapshot(
            parsedClassSheets,
            skippedClassSheets,
            emptyClassSheets,
            diagnosticFailureCount);

        result.Should().BeTrue();
    }
}
