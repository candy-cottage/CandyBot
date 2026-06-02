using System.Text.Json;
using CandyCottage.CandyBot.Services;
using FluentAssertions;
using Xunit;

namespace CandyBot.Tests.Services;

public class VehicleSheetParserTests
{
    private static Dictionary<string, List<List<string>>> LoadFixture()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Forza", "DSXRieHNsZXpFZmZ4.json");
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<string, List<List<string>>>>(json)!;
    }

    [Fact]
    public void Parse_同名Purist表头不覆盖主表列_B600公路恢复17辆()
    {
        Dictionary<string, List<List<string>>> sheets = LoadFixture();

        VehicleSheetParseResult result = VehicleSheetParser.Parse("B-600公路", sheets["B-600公路"]);

        result.Vehicles.Should().HaveCount(17);
        result.Vehicles.Should().Contain(v => v.Manufacturer == "ACURA 讴歌" && v.Model == "Integra Type R");
        result.Vehicles.Should().NotContain(v => v.Manufacturer == "本田" && v.Model == "Civic Type R 2004",
            "右侧 Purist 专区不应被平铺进常规 Vehicles");
    }

    [Fact]
    public void Parse_空型号且有调校的合并子行继承上一辆车()
    {
        Dictionary<string, List<List<string>>> sheets = LoadFixture();

        VehicleSheetParseResult result = VehicleSheetParser.Parse("S1-800公路", sheets["S1-800公路"]);

        result.Vehicles.Should().Contain(v =>
            v.Manufacturer == "AUDI奥迪" &&
            v.Model == "R8LMS" &&
            v.Tune == "CybrexzzZ[META]" &&
            v.LapTime != null &&
            v.LapTime.Contains("白川54.5"));
    }

    [Fact]
    public void Parse_缺型号且无法继承的行计入Skipped而不是抛异常()
    {
        List<List<string>> grid =
        [
            ["比赛类型：公路"],
            ["制造商", "型号", "年份", "传动", "属性", "胎面", "干/湿地", "强度", "调校", "备注", "圈速测试"],
            ["Ford 福特", "", "2024", "四驱", "均衡", "", "通用", "", "", "", ""],
        ];

        VehicleSheetParseResult result = VehicleSheetParser.Parse("A-700公路", grid);

        result.Vehicles.Should().BeEmpty();
        result.SkippedRows.Should().ContainSingle(row => row.Reason.Contains("型号"));
    }
}
