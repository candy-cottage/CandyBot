using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Services.Esports;
using FluentAssertions;
using Xunit;

namespace CandyBot.Tests.Services.Esports;

public class PandaScoreProviderTests
{
    [Fact]
    public void MapMatch_两队未开赛比赛_映射为外部比赛草稿()
    {
        const string json = """
        {
          "id": 647619,
          "name": "NAVI vs FaZe",
          "status": "not_started",
          "scheduled_at": "2026-06-10T12:00:00Z",
          "begin_at": "2026-06-10T12:00:00Z",
          "end_at": null,
          "league": { "id": 11, "name": "ESL Pro League" },
          "serie": { "id": 22, "full_name": "Season 23" },
          "tournament": { "id": 33, "name": "Group Stage" },
          "opponents": [
            { "opponent": { "id": 100, "name": "NAVI" }, "type": "Team" },
            { "opponent": { "id": 200, "name": "FaZe" }, "type": "Team" }
          ],
          "winner_id": null,
          "results": []
        }
        """;

        ExternalEsportsMatch match = PandaScoreMapper.MapMatch(json);

        match.ExternalId.Should().Be("647619");
        match.EventExternalId.Should().Be("11:22:33");
        match.EventName.Should().Be("ESL Pro League Season 23");
        match.Team1Name.Should().Be("NAVI");
        match.Team2Name.Should().Be("FaZe");
        match.StartTime.Should().Be(DateTime.Parse("2026-06-10T12:00:00Z").ToUniversalTime());
        match.Stage.Should().Be("Group Stage");
        match.Team1WinProbability.Should().BeNull("fixtures/statistics API does not provide bookmaker odds by default");
    }

    [Fact]
    public void MapResult_已结束比赛_映射winner与比分()
    {
        const string json = """
        {
          "id": 647619,
          "status": "finished",
          "winner_id": 100,
          "opponents": [
            { "opponent": { "id": 100, "name": "NAVI" }, "type": "Team" },
            { "opponent": { "id": 200, "name": "FaZe" }, "type": "Team" }
          ],
          "results": [
            { "team_id": 100, "score": 2 },
            { "team_id": 200, "score": 1 }
          ]
        }
        """;

        ExternalEsportsResult result = PandaScoreMapper.MapResult(json);

        result.ExternalId.Should().Be("647619");
        result.Winner.Should().Be(1);
        result.ResultText.Should().Be("2:1");
    }
}
