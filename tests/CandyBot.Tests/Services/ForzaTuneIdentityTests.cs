using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Utilities;
using FluentAssertions;
using Xunit;

namespace CandyBot.Tests.Services;

public class ForzaTuneIdentityTests
{
    // ── Task-2 新测试 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 构造一个基准 VehicleTuneRequest，便于各测试只覆盖关注字段。
    /// </summary>
    private static VehicleTuneRequest MakeTune(
        string manufacturer = "Lamborghini",
        string model = "Huracán",
        string? year = "2014",
        string? @class = "S1",
        string? matchType = "Road",
        string? drivetrain = "AWD",
        string? performancePreference = "均衡",
        string? tireCompound = "半热熔",
        string? surfaceCondition = "干地",
        string? strength = "中等",
        string? tuneRaw = "LyricismRain",
        string? remarks = null,
        string? lapTime = null) => new()
    {
        Manufacturer = manufacturer,
        Model = model,
        Year = year,
        Class = @class,
        MatchType = matchType,
        Drivetrain = drivetrain,
        PerformancePreference = performancePreference,
        TireCompound = tireCompound,
        SurfaceCondition = surfaceCondition,
        Strength = strength,
        TuneRaw = tuneRaw,
        Remarks = remarks,
        LapTime = lapTime,
    };

    [Fact]
    public void BuildModelKey_Year为null与空串结果相同()
    {
        var withNull = ForzaTuneIdentity.BuildModelKey("Lamborghini", "Huracán", null);
        var withEmpty = ForzaTuneIdentity.BuildModelKey("Lamborghini", "Huracán", "");
        withNull.Should().Be(withEmpty);
    }

    [Fact]
    public void BuildSourceIdentity_仅SurfaceCondition不同时身份相同()
    {
        var fileId = "DSXRieHNsZXpFZmZ4";
        var modelKey = ForzaTuneIdentity.BuildModelKey("Lamborghini", "Huracán", "2014");
        var tuneA = MakeTune(surfaceCondition: "干地");
        var tuneB = MakeTune(surfaceCondition: "湿地");

        var idA = ForzaTuneIdentity.BuildSourceIdentity(tuneA, modelKey, fileId);
        var idB = ForzaTuneIdentity.BuildSourceIdentity(tuneB, modelKey, fileId);

        idA.Should().Be(idB, "SurfaceCondition 不在 identity 的 6 个字段中");
    }

    [Fact]
    public void BuildFingerprint_仅SurfaceCondition不同时指纹不同()
    {
        var modelKey = ForzaTuneIdentity.BuildModelKey("Lamborghini", "Huracán", "2014");
        var tuneA = MakeTune(surfaceCondition: "干地");
        var tuneB = MakeTune(surfaceCondition: "湿地");

        var fpA = ForzaTuneIdentity.BuildFingerprint(tuneA, modelKey);
        var fpB = ForzaTuneIdentity.BuildFingerprint(tuneB, modelKey);

        fpA.Should().NotBe(fpB, "SurfaceCondition 变化应翻转指纹");
    }

    [Fact]
    public void BuildSourceIdentity_改变车型后身份必变()
    {
        var fileId = "DSXRieHNsZXpFZmZ4";
        var modelKeyA = ForzaTuneIdentity.BuildModelKey("Lamborghini", "Huracán", "2014");
        var modelKeyB = ForzaTuneIdentity.BuildModelKey("Ferrari", "488 GTB", "2015");
        var tune = MakeTune();

        var idA = ForzaTuneIdentity.BuildSourceIdentity(tune, modelKeyA, fileId);
        var idB = ForzaTuneIdentity.BuildSourceIdentity(tune, modelKeyB, fileId);

        idA.Should().NotBe(idB, "不同车型（modelKey）必须产生不同的 SourceIdentity");
    }

    [Fact]
    public void BuildFingerprint_改变车型后指纹必变()
    {
        var modelKeyA = ForzaTuneIdentity.BuildModelKey("Lamborghini", "Huracán", "2014");
        var modelKeyB = ForzaTuneIdentity.BuildModelKey("Ferrari", "488 GTB", "2015");
        var tune = MakeTune();

        var fpA = ForzaTuneIdentity.BuildFingerprint(tune, modelKeyA);
        var fpB = ForzaTuneIdentity.BuildFingerprint(tune, modelKeyB);

        fpA.Should().NotBe(fpB);
    }

    // ── Task-1 原有测试（保留不变）────────────────────────────────────────────

    [Theory]
    [InlineData("LyricismRain", new[] { "lyricismrain" })]
    [InlineData("K1Z bala, LetzeLU", new[] { "k1z bala", "letzelu" })]
    [InlineData("CybrexzzZ，Cirno1X[META+]", new[] { "cybrexzzz", "cirno1x" })]                 // 全角逗号
    [InlineData("Mustuff124，CybrexzzZ[THE WHEELCHAIR]，Cirno1X[META+]", new[] { "mustuff124", "cybrexzzz", "cirno1x" })]
    [InlineData("LetzeLU[AWD Road]\nnyasmowisher[AWD grip meta V2]", new[] { "letzelu", "nyasmowisher" })]
    [InlineData("OSM Cinnabar[V2.1]", new[] { "osm cinnabar" })]                                // 含空格 id + 方括号关键字
    [InlineData("SakuraMxSPD[new] （半热熔）", new[] { "sakuramxspd" })]                         // 一 token 多括号组
    [InlineData("[681478665]", new[] { "681478665" })]
    [InlineData(" PTW KDEdward", new[] { "ptw kdedward" })]
    [InlineData("Shinnonsuke[?]", new[] { "shinnonsuke" })]
    public void ParseTuners_提取调校师并归一化(string cell, string[] expectedNormIds) =>
        ForzaTuneIdentity.ParseTuners(cell).Select(t => t.IdNormalized).Should().Equal(expectedNormIds);

    [Fact]
    public void ParseTuners_书写顺序经TunerKey后等价()
    {
        var a = ForzaTuneIdentity.TunerKey(ForzaTuneIdentity.ParseTuners("Cirno1X，CybrexzzZ"));
        var b = ForzaTuneIdentity.TunerKey(ForzaTuneIdentity.ParseTuners("CybrexzzZ，Cirno1X"));
        a.Should().Be(b);
    }

    [Fact]
    public void ParseTuners_保留括号关键字而不并入Id()
    {
        var t = ForzaTuneIdentity.ParseTuners("Dack86（全热熔）").Single();
        t.Id.Should().Be("Dack86"); t.Keyword.Should().Be("全热熔");
    }
}
