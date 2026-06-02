using System.Data;
using CandyCottage.CandyBot.Services;
using FluentAssertions;
using Xunit;

namespace CandyBot.Tests.Services;

/// <summary>
/// 针对 <see cref="ForzaService.RenderVehiclesAsCards"/> 纯渲染逻辑的单元测试（不依赖数据库）。
///
/// 关注点（这是查车/查等级结果改成卡片布局后最易出错的地方）：
/// <list type="bullet">
///   <item>完整字段 → 逐行卡片的确切版式；</item>
///   <item>空字段会被整段省略（不留悬挂的 <c>|</c> 分隔符或孤立标签）；</item>
///   <item>标题行带 ID（管理员据此「修改车辆 &lt;ID&gt;」）；</item>
///   <item>多辆车之间有分隔线，且结尾不残留分隔线。</item>
/// </list>
/// </summary>
public class ForzaCardRenderingTests
{
    /// <summary>渲染器只读列的字符串值，因此测试用全 string 列即可如实复现其行为。</summary>
    private static readonly string[] Columns =
    [
        "Id", "Class", "MatchType", "Manufacturer", "Model", "Year", "Drivetrain",
        "Attribute", "TireCompound", "SurfaceCondition", "Strength", "Tune", "Remarks", "LapTime",
    ];

    private static DataTable NewTable()
    {
        DataTable dt = new();
        foreach (string col in Columns) dt.Columns.Add(col, typeof(string));
        return dt;
    }

    /// <summary>追加一行车辆；未指定的字段默认空串（即"该字段缺省"）。</summary>
    private static void AddRow(DataTable dt, string id = "", string cls = "", string matchType = "",
        string manufacturer = "", string model = "", string year = "", string drivetrain = "",
        string attribute = "", string tire = "", string surface = "", string strength = "",
        string tune = "", string remarks = "", string lapTime = "")
    {
        DataRow r = dt.NewRow();
        r["Id"] = id; r["Class"] = cls; r["MatchType"] = matchType;
        r["Manufacturer"] = manufacturer; r["Model"] = model; r["Year"] = year;
        r["Drivetrain"] = drivetrain; r["Attribute"] = attribute; r["TireCompound"] = tire;
        r["SurfaceCondition"] = surface; r["Strength"] = strength; r["Tune"] = tune;
        r["Remarks"] = remarks; r["LapTime"] = lapTime;
        dt.Rows.Add(r);
    }

    /// <summary>
    /// 按行切分渲染结果，并丢弃空行。渲染器用 <c>AppendLine</c>（= <c>Environment.NewLine</c>）
    /// 收尾，会留一个尾随换行；统一归一化换行符后按行断开，断言才不依赖运行平台。
    /// </summary>
    private static string[] Lines(string s) =>
        s.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void 完整字段_渲染出预期的逐行卡片()
    {
        DataTable dt = NewTable();
        AddRow(dt, id: "123", cls: "S1", matchType: "公路", manufacturer: "法拉利", model: "F40",
            year: "1992", drivetrain: "四驱", attribute: "均衡", tire: "半热熔", surface: "干",
            strength: "META+", tune: "Nyasmowisher[be my light]", remarks: "手感极佳", lapTime: "白川54.5");

        string[] lines = Lines(ForzaService.RenderVehiclesAsCards(dt));

        lines.Should().Equal(
            "🏎 法拉利 F40 (1992) · S1/公路 · ID 123",
            "四驱 | 均衡 | 强度 META+",
            "胎面 半热熔 · 干",
            "调校 Nyasmowisher[be my light]",
            "备注 手感极佳",
            "圈速 白川54.5");
    }

    [Fact]
    public void 仅必填字段_只渲染标题且不留悬挂分隔符或空标签()
    {
        // 一辆只填了必填字段（无年份/传动/强度/胎面/调校/圈速……）的车
        DataTable dt = NewTable();
        AddRow(dt, id: "7", cls: "A", matchType: "公路", manufacturer: "本田", model: "思域");

        string result = ForzaService.RenderVehiclesAsCards(dt);
        string[] lines = Lines(result);

        // 整张卡片应只剩标题一行（年份缺省 → 标题里也不出现括号）
        lines.Should().ContainSingle().Which.Should().Be("🏎 本田 思域 · A/公路 · ID 7");

        // 不得残留任何空字段留下的痕迹
        result.Should().NotContain(" | ", "传动/属性/强度缺省时不应留下悬挂的竖线分隔符");
        result.Should().NotContainAny("强度", "胎面", "调校", "备注", "圈速", "(", ")");
    }

    [Fact]
    public void 标题行携带ID_供管理员据此修改车辆()
    {
        DataTable dt = NewTable();
        AddRow(dt, id: "456", cls: "S2", matchType: "越野", manufacturer: "福特", model: "Bronco");

        string[] lines = Lines(ForzaService.RenderVehiclesAsCards(dt));

        lines[0].Should().EndWith("· ID 456");
    }

    [Fact]
    public void 中间字段缺省_JoinNonEmpty跳过空段而非留下双分隔符()
    {
        // 传动有、属性缺、强度有 → 第二行应是「后驱 | 强度 META」而非「后驱 |  | 强度 META」
        DataTable dt = NewTable();
        AddRow(dt, id: "9", cls: "A", matchType: "公路", manufacturer: "马自达", model: "MX-5",
            drivetrain: "后驱", strength: "META");

        string result = ForzaService.RenderVehiclesAsCards(dt);

        Lines(result)[1].Should().Be("后驱 | 强度 META");
        result.Should().NotContain(" |  | ", "中间字段缺省时不应产生连续的空分隔符");
    }

    [Fact]
    public void 多辆车_卡片间有分隔线且结尾不残留分隔线()
    {
        DataTable dt = NewTable();
        AddRow(dt, id: "7", cls: "A", matchType: "公路", manufacturer: "本田", model: "思域");
        AddRow(dt, id: "8", cls: "B", matchType: "公路", manufacturer: "丰田", model: "AE86");

        string[] lines = Lines(ForzaService.RenderVehiclesAsCards(dt));

        // 结构：标题1 / 分隔线 / 标题2
        lines.Should().HaveCount(3);
        lines[0].Should().StartWith("🏎");
        lines[1].Should().MatchRegex("^━+$", "两张卡片之间应是一整行分隔线");
        lines[2].Should().StartWith("🏎");
        // 最后一行是车辆卡片而非分隔线 → 结尾不残留分隔线
        lines[^1].Should().NotMatchRegex("^━+$");
    }
}
