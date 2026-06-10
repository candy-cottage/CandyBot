using CandyCottage.CandyBot.Services;
using FluentAssertions;
using Xunit;

namespace CandyBot.Tests.Services;

/// <summary>
/// SplitPageParam：查车 / 查赛道 / 查等级 共用的 “-p / --page 页码” 解析。
/// </summary>
public class PageParamTests
{
    [Theory]
    [InlineData("法拉利 -p 2", "法拉利", 2)]
    [InlineData("法拉利 --page 3", "法拉利", 3)]
    [InlineData("法拉利", "法拉利", 1)]
    [InlineData("911", "911", 1)] // 数字车名不再被误当成页码（旧的尾数字格式的已知缺陷）
    [InlineData("Aston Martin DB11 -p 12", "Aston Martin DB11", 12)]
    [InlineData("", "", 1)]
    [InlineData("-p 2", "", 2)] // 只有页码没有关键字 → 关键字为空，由调用方提示格式
    [InlineData("法拉利 -p 0", "法拉利", 1)] // 无效页码按未指定处理
    [InlineData("法拉利 -p 99999999999999999999", "法拉利", 1)] // 超出 int 范围不抛异常
    public void 解析关键字与页码(string detail, string expectedQuery, int expectedPage)
    {
        (string query, int page) = CandyBotService.SplitPageParam(detail);

        query.Should().Be(expectedQuery);
        page.Should().Be(expectedPage);
    }
}
