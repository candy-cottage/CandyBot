using System.Net;
using CandyBot.IntegrationTests.Infrastructure;
using CandyCottage.CandyBot.Utilities;
using FluentAssertions;
using Xunit;

namespace CandyBot.IntegrationTests.Betting;

/// <summary>
/// 针对 <c>BettingController</c> 四个匿名 GET 端点的真实 MySQL 端到端可见性集成测试：
/// 验证未发布草稿（<c>is_published=0</c>，含预填的 result/winner）<b>不会</b>从公开 HTTP API 泄露。
///
/// <para>
/// 复用 <see cref="MySqlWebApplicationFactory"/>（HTTP 宿主直连容器、不替换 SQLHelper）+
/// <see cref="MySqlFixture"/>（用 <see cref="MySqlFixture.CreateHelper"/> 种子真实行），
/// 镜像 <c>ApiTokenAuthTests</c> 的端到端做法。
/// </para>
/// </summary>
[Collection(MySqlCollection.Name)]
[Trait("Category", "MySql")]
public sealed class BettingControllerDraftVisibilityTests : IAsyncLifetime
{
    private readonly MySqlFixture _fixture;
    private readonly MySqlWebApplicationFactory _factory;

    private long _publishedEventId;
    private long _draftEventId;
    private long _publishedMatchId;
    private long _draftMatchId;

    public BettingControllerDraftVisibilityTests(MySqlFixture fixture)
    {
        _fixture = fixture;
        _factory = new MySqlWebApplicationFactory(
            fixture.Host, fixture.Port, fixture.Database, fixture.Username, fixture.ConnectionPassword);
    }

    public async Task InitializeAsync()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();

        // 已发布赛事 + 比赛（对匿名用户可见）。
        sql.Execute("INSERT INTO betting_events (external_id, is_published, name, status, start_time, end_time) VALUES (NULL, 1, '已发布赛事PUB', 0, NOW(), DATE_ADD(NOW(), INTERVAL 1 DAY))");
        _publishedEventId = sql.LastInsertId;
        sql.Parameters["@eid"] = _publishedEventId;
        sql.Execute(@"INSERT INTO betting_matches (external_id, is_published, event_id, team1_name, team2_name, stage, status, start_time, bet_deadline, available_options, team1_win_odds, team2_win_odds, description, betting_enabled)
            VALUES (NULL, 1, @eid, 'PubA', 'PubB', '小组赛', 0, DATE_ADD(NOW(), INTERVAL 1 DAY), DATE_ADD(NOW(), INTERVAL 1 DAY), '[""team1_win"",""team2_win""]', 1.85, 1.85, '', 1)");
        _publishedMatchId = sql.LastInsertId;

        // 未发布草稿赛事 + 比赛（含预填 result/winner），必须对匿名用户不可见。
        sql.Execute("INSERT INTO betting_events (external_id, is_published, name, status, start_time, end_time) VALUES ('evt-draft', 0, '草稿赛事DRAFT', 0, NOW(), DATE_ADD(NOW(), INTERVAL 1 DAY))");
        _draftEventId = sql.LastInsertId;
        sql.Parameters["@eid"] = _draftEventId;
        sql.Execute(@"INSERT INTO betting_matches (external_id, is_published, pending_settlement, event_id, team1_name, team2_name, stage, status, start_time, bet_deadline, available_options, team1_win_odds, team2_win_odds, result, winner, description, betting_enabled)
            VALUES ('match-draft', 0, 1, @eid, 'DraftA', 'DraftB', '草稿', 0, DATE_ADD(NOW(), INTERVAL 1 DAY), DATE_ADD(NOW(), INTERVAL 1 DAY), '[""team1_win"",""team2_win""]', 1.85, 1.85, '1:0', 1, '', 1)");
        _draftMatchId = sql.LastInsertId;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetEvents_只返回已发布赛事_不泄露草稿()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage resp = await client.GetAsync("/betting/events");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("已发布赛事PUB").And.NotContain("草稿赛事DRAFT");
    }

    [Fact]
    public async Task GetMatches_只返回已发布比赛_不泄露草稿()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage resp = await client.GetAsync("/betting/matches");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("PubA").And.NotContain("DraftA");
    }

    [Fact]
    public async Task GetEvent_已发布返回200_草稿返回404()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage published = await client.GetAsync($"/betting/events/{_publishedEventId}");
        published.StatusCode.Should().Be(HttpStatusCode.OK);

        HttpResponseMessage draft = await client.GetAsync($"/betting/events/{_draftEventId}");
        draft.StatusCode.Should().Be(HttpStatusCode.NotFound, "未发布草稿赛事对匿名用户应不存在");
    }

    [Fact]
    public async Task GetMatch_已发布返回200_草稿返回404()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage published = await client.GetAsync($"/betting/matches/{_publishedMatchId}");
        published.StatusCode.Should().Be(HttpStatusCode.OK);

        HttpResponseMessage draft = await client.GetAsync($"/betting/matches/{_draftMatchId}");
        draft.StatusCode.Should().Be(HttpStatusCode.NotFound, "未发布草稿比赛（含预填 result/winner）对匿名用户应不存在");
    }
}
