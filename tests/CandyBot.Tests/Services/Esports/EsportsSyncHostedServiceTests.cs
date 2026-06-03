using CandyCottage.CandyBot.Services.Esports;
using FluentAssertions;
using Xunit;

namespace CandyBot.Tests.Services.Esports;

public class EsportsSyncHostedServiceTests
{
    private static readonly DateTime Now = new(2026, 6, 3, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void DecideSyncs_自动化关闭_两项均不运行()
    {
        // 即便两个 next* 都早于 now，自动化关闭时也不应触发任何同步。
        EsportsSyncHostedService.DecideSyncs(false, Now, DateTime.MinValue, DateTime.MinValue)
            .Should().Be((false, false));
    }

    [Fact]
    public void DecideSyncs_自动化开启且节律已到_两项均运行()
    {
        // 首个启用 tick：next* = MinValue（过去），赛程与结果都应运行。
        EsportsSyncHostedService.DecideSyncs(true, Now, DateTime.MinValue, DateTime.MinValue)
            .Should().Be((true, true));
    }

    [Fact]
    public void DecideSyncs_赛程未到结果已到_仅运行结果_证明节律独立()
    {
        // 赛程下次时间在未来、结果下次时间在过去：两条节律互不影响，仅结果运行。
        EsportsSyncHostedService.DecideSyncs(true, Now, Now.AddMinutes(10), DateTime.MinValue)
            .Should().Be((false, true));
    }

    [Fact]
    public void DecideSyncs_两项均未到_两项均不运行()
    {
        EsportsSyncHostedService.DecideSyncs(true, Now, Now.AddMinutes(10), Now.AddMinutes(5))
            .Should().Be((false, false));
    }
}
