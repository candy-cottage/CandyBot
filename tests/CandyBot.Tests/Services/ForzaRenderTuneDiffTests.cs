using System.Data;
using CandyCottage.CandyBot.Services;
using FluentAssertions;
using Xunit;

namespace CandyBot.Tests.Services;

/// <summary>
/// 针对 <see cref="ForzaService.RenderTuneDiff"/> 的单元测试（Task 16）。
/// 纯内存操作，不依赖数据库。
/// </summary>
public class ForzaRenderTuneDiffTests
{
    /// <summary>构造一个带有调校显示字段列的 DataTable，并插入一行。</summary>
    private static DataRow MakeRow(
        string cls = "S1", string matchType = "公路", string drivetrain = "四驱",
        string perfPref = "", string tireCompound = "", string surfCond = "",
        string strength = "", string tuneRaw = "", string remarks = "", string lapTime = "",
        string extData = "")
    {
        DataTable dt = new();
        dt.Columns.Add("Class");
        dt.Columns.Add("MatchType");
        dt.Columns.Add("Drivetrain");
        dt.Columns.Add("PerformancePreference");
        dt.Columns.Add("TireCompound");
        dt.Columns.Add("SurfaceCondition");
        dt.Columns.Add("Strength");
        dt.Columns.Add("TuneRaw");
        dt.Columns.Add("Remarks");
        dt.Columns.Add("LapTime");
        dt.Columns.Add("ExtData");

        DataRow row = dt.NewRow();
        row["Class"] = cls;
        row["MatchType"] = matchType;
        row["Drivetrain"] = drivetrain;
        row["PerformancePreference"] = perfPref;
        row["TireCompound"] = tireCompound;
        row["SurfaceCondition"] = surfCond;
        row["Strength"] = strength;
        row["TuneRaw"] = tuneRaw;
        row["Remarks"] = remarks;
        row["LapTime"] = lapTime;
        row["ExtData"] = extData;
        dt.Rows.Add(row);
        return row;
    }

    [Fact]
    public void RenderTuneDiff_有变化字段_只输出变化行()
    {
        // 仅 Remarks 不同
        DataRow live = MakeRow(cls: "S1", remarks: "旧备注");
        DataRow cache = MakeRow(cls: "S1", remarks: "新备注");

        string diff = ForzaService.RenderTuneDiff(live, cache);

        diff.Should().Contain("备注：旧备注 -> 新备注");
        diff.Should().NotContain("等级：");   // Class 相同，不应输出
        diff.Should().NotContain("比赛类型："); // 相同
    }

    [Fact]
    public void RenderTuneDiff_无变化_返回空串()
    {
        DataRow live = MakeRow(cls: "A", matchType: "拉力", drivetrain: "后驱", remarks: "备注");
        DataRow cache = MakeRow(cls: "A", matchType: "拉力", drivetrain: "后驱", remarks: "备注");

        string diff = ForzaService.RenderTuneDiff(live, cache);

        diff.Should().BeEmpty("所有字段相同时应返回空串");
    }

    [Fact]
    public void RenderTuneDiff_多字段变化_全部输出()
    {
        DataRow live = MakeRow(cls: "S1", drivetrain: "四驱", remarks: "原始", lapTime: "1:23.456");
        DataRow cache = MakeRow(cls: "A", drivetrain: "后驱", remarks: "更新", lapTime: "1:20.000");

        string diff = ForzaService.RenderTuneDiff(live, cache);

        diff.Should().Contain("等级：S1 -> A");
        diff.Should().Contain("驱动：四驱 -> 后驱");
        diff.Should().Contain("备注：原始 -> 更新");
        diff.Should().Contain("圈速：1:23.456 -> 1:20.000");
    }
}
