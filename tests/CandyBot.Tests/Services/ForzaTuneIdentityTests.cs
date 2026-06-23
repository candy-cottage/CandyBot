using CandyCottage.CandyBot.Utilities;
using FluentAssertions;
using Xunit;

namespace CandyBot.Tests.Services;

public class ForzaTuneIdentityTests
{
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
