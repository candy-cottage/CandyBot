using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Utilities;
using Microsoft.Extensions.Caching.Memory;

namespace CandyCottage.CandyBot.Services.Esports;

public sealed class PandaScoreProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    PandaScoreConfig config,
    ILogger<PandaScoreProvider> logger) : IEsportsDataProvider, IEsportsScopeSearchProvider
{
    public async Task<IReadOnlyList<ExternalEsportsMatch>> GetUpcomingMatchesAsync(string gameSlug, DateTime since, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            logger.LogWarning("PandaScore API Key 未配置，跳过赛程同步");
            return [];
        }

        IReadOnlyList<string> urls = BuildUpcomingMatchUrls(gameSlug, since);
        string cacheKey = $"pandascore:upcoming:{gameSlug}:{since:yyyyMMddHH}:{string.Join("|", urls)}";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<ExternalEsportsMatch>? cached) && cached is not null)
        {
            return cached;
        }

        List<ExternalEsportsMatch> result = [];
        foreach (string url in urls)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);

            string json = await SendAsync(request, cancellationToken);
            result.AddRange(PandaScoreMapper.MapMatches(json));
        }

        // 同一场比赛可能同时属于 league/serie/tournament 作用域，入库前按外部比赛 ID 合并。
        IReadOnlyList<ExternalEsportsMatch> unique = urls.Count == 1
            ? result
            : result
                .GroupBy(match => match.ExternalId)
                .Select(group => group.First())
                .OrderBy(match => match.StartTime)
                .ToList();
        cache.Set(cacheKey, unique, TimeSpan.FromMinutes(5));
        return unique;
    }

    public async Task<IReadOnlyList<ExternalEsportsResult>> GetFinishedResultsAsync(IReadOnlyCollection<string> externalIds, CancellationToken cancellationToken = default)
    {
        if (externalIds.Count == 0 || string.IsNullOrWhiteSpace(config.ApiKey))
        {
            return [];
        }

        List<ExternalEsportsResult> results = [];
        foreach (string[] batch in externalIds.Distinct().Chunk(50))
        {
            string batchKey = string.Join(",", batch);
            string cacheKey = $"pandascore:results:{batchKey}";
            if (cache.TryGetValue(cacheKey, out IReadOnlyList<ExternalEsportsResult>? cached) && cached is not null)
            {
                results.AddRange(cached);
                continue;
            }

            string url = $"{config.BaseUrl.TrimEnd('/')}/{config.GameSlug}/matches?filter[id]={Uri.EscapeDataString(batchKey)}&filter[status]=finished&page[size]=100";
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);

            string json = await SendAsync(request, cancellationToken);
            List<ExternalEsportsResult> batchResults = PandaScoreMapper.MapResults(json)
                .Where(result => result.Winner is 1 or 2)
                .ToList();
            cache.Set(cacheKey, batchResults, TimeSpan.FromMinutes(15));
            results.AddRange(batchResults);
        }
        return results;
    }

    public async Task<IReadOnlyList<ExternalEsportsScope>> SearchScopesAsync(string gameSlug, string keyword, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey) || string.IsNullOrWhiteSpace(keyword))
        {
            return [];
        }

        string cacheKey = $"pandascore:scope-search:{gameSlug}:{keyword.Trim().ToLowerInvariant()}";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<ExternalEsportsScope>? cached) && cached is not null)
        {
            return cached;
        }

        List<ExternalEsportsScope> result = [];
        string baseUrl = config.BaseUrl.TrimEnd('/');
        string query = $"search[name]={Uri.EscapeDataString(keyword.Trim())}&page[size]=5";
        foreach ((string type, string resource) in new[] { ("league", "videogames/{game}/leagues"), ("serie", "videogames/{game}/series"), ("tournament", "tournaments") })
        {
            string path = resource.Replace("{game}", Uri.EscapeDataString(gameSlug), StringComparison.Ordinal);
            string url = $"{baseUrl}/{path}?{query}";
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);

            string json = await SendAsync(request, cancellationToken);
            result.AddRange(PandaScoreMapper.MapScopes(json, type));
        }

        IReadOnlyList<ExternalEsportsScope> scopes = result
            .GroupBy(scope => (scope.Type, scope.Value), StringTupleComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(10)
            .ToList();
        cache.Set(cacheKey, scopes, TimeSpan.FromMinutes(10));
        return scopes;
    }

    private async Task<string> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>
    /// 有赛事作用域配置时改用 PandaScore 层级端点；未配置时保持原有按游戏全量拉取。
    /// </summary>
    private IReadOnlyList<string> BuildUpcomingMatchUrls(string gameSlug, DateTime since)
    {
        string baseUrl = config.BaseUrl.TrimEnd('/');
        string query = BuildUpcomingMatchQuery(since);
        List<string> urls = [];
        AddScopedMatchUrls(urls, baseUrl, "leagues", config.LeagueIdsOrSlugs, query);
        AddScopedMatchUrls(urls, baseUrl, "series", config.SerieIdsOrSlugs, query);
        AddScopedMatchUrls(urls, baseUrl, "tournaments", config.TournamentIdsOrSlugs, query);

        if (urls.Count == 0)
        {
            urls.Add($"{baseUrl}/{Uri.EscapeDataString(gameSlug)}/matches?{query}");
        }

        return urls;
    }

    private static string BuildUpcomingMatchQuery(DateTime since)
        => $"filter[status]=not_started&range[scheduled_at]={Uri.EscapeDataString(since.ToString("O"))},{Uri.EscapeDataString(since.AddDays(30).ToString("O"))}&sort=scheduled_at&page[size]=100";

    private static void AddScopedMatchUrls(List<string> urls, string baseUrl, string resource, string configuredScopes, string query)
    {
        foreach (string scope in SplitScopes(configuredScopes))
        {
            urls.Add($"{baseUrl}/{resource}/{Uri.EscapeDataString(scope)}/matches?{query}");
        }
    }

    private static IEnumerable<string> SplitScopes(string configuredScopes)
        => configuredScopes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Distinct(StringComparer.OrdinalIgnoreCase);
}

internal static class PandaScoreMapper
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static List<ExternalEsportsMatch> MapMatches(string json)
        => JsonSerializer.Deserialize<List<PandaScoreMatch>>(json, Options)?
            .Select(MapMatch)
            .Where(match => !string.IsNullOrWhiteSpace(match.Team1Name) && !string.IsNullOrWhiteSpace(match.Team2Name))
            .ToList() ?? [];

    public static ExternalEsportsMatch MapMatch(string json)
        => MapMatch(JsonSerializer.Deserialize<PandaScoreMatch>(json, Options) ?? throw new InvalidOperationException("PandaScore match JSON 为空。"));

    public static ExternalEsportsResult MapResult(string json)
        => MapResult(JsonSerializer.Deserialize<PandaScoreMatch>(json, Options) ?? throw new InvalidOperationException("PandaScore result JSON 为空。"));

    public static List<ExternalEsportsResult> MapResults(string json)
        => JsonSerializer.Deserialize<List<PandaScoreMatch>>(json, Options)?
            .Select(MapResult)
            .ToList() ?? [];

    public static List<ExternalEsportsScope> MapScopes(string json, string type)
        => JsonSerializer.Deserialize<List<PandaScoreScopeEntity>>(json, Options)?
            .Select(scope => MapScope(scope, type))
            .Where(scope => !string.IsNullOrWhiteSpace(scope.Value))
            .ToList() ?? [];

    private static ExternalEsportsMatch MapMatch(PandaScoreMatch source)
    {
        PandaScoreOpponent? team1 = (source.Opponents ?? []).ElementAtOrDefault(0)?.Opponent;
        PandaScoreOpponent? team2 = (source.Opponents ?? []).ElementAtOrDefault(1)?.Opponent;
        DateTime start = source.ScheduledAt ?? source.BeginAt ?? DateTime.UtcNow;
        DateTime utcStart = start.Kind switch
        {
            DateTimeKind.Utc => start,
            DateTimeKind.Local => start.ToUniversalTime(),
            _ => DateTime.SpecifyKind(start, DateTimeKind.Utc),
        };
        DateTime beijingStart = ChinaStandardTime.FromUtc(utcStart);
        string league = source.League?.Name ?? "";
        string serie = source.Serie?.FullName ?? source.Serie?.Name ?? "";
        string eventName = string.Join(" ", new[] { league, serie }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
        if (string.IsNullOrWhiteSpace(eventName)) eventName = source.Name;

        return new ExternalEsportsMatch(
            ExternalId: source.Id.ToString(),
            EventExternalId: $"{source.League?.Id}:{source.Serie?.Id}:{source.Tournament?.Id}",
            EventName: eventName,
            EventStartTime: beijingStart.Date,
            EventEndTime: beijingStart.Date.AddDays(1),
            Team1Name: team1?.Name ?? "",
            Team2Name: team2?.Name ?? "",
            StartTime: utcStart,
            Stage: source.Tournament?.Name ?? "",
            Team1WinProbability: null,
            Team1WinOdds: null,
            Team2WinOdds: null);
    }

    private static ExternalEsportsResult MapResult(PandaScoreMatch source)
    {
        long? team1Id = (source.Opponents ?? []).ElementAtOrDefault(0)?.Opponent?.Id;
        long? team2Id = (source.Opponents ?? []).ElementAtOrDefault(1)?.Opponent?.Id;
        int winner = source.WinnerId == team1Id ? 1 : source.WinnerId == team2Id ? 2 : 0;
        int score1 = (source.Results ?? []).FirstOrDefault(r => r.TeamId == team1Id)?.Score ?? 0;
        int score2 = (source.Results ?? []).FirstOrDefault(r => r.TeamId == team2Id)?.Score ?? 0;
        return new(source.Id.ToString(), $"{score1}:{score2}", winner);
    }

    private static ExternalEsportsScope MapScope(PandaScoreScopeEntity source, string type)
    {
        string name = source.FullName ?? source.Name ?? "";
        string value = !string.IsNullOrWhiteSpace(source.Slug) ? source.Slug! : source.Id?.ToString() ?? "";
        string? parent = type switch
        {
            "serie" => source.League?.Name,
            "tournament" => source.Serie?.FullName ?? source.Serie?.Name ?? source.League?.Name,
            _ => null,
        };
        return new(type, name, value, source.Id, parent);
    }
}

internal sealed class StringTupleComparer : IEqualityComparer<(string Type, string Value)>
{
    public static StringTupleComparer OrdinalIgnoreCase { get; } = new();

    public bool Equals((string Type, string Value) x, (string Type, string Value) y)
        => string.Equals(x.Type, y.Type, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Value, y.Value, StringComparison.OrdinalIgnoreCase);

    public int GetHashCode((string Type, string Value) obj)
        => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Type), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Value));
}

internal sealed record PandaScoreMatch(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("scheduled_at")] DateTime? ScheduledAt,
    [property: JsonPropertyName("begin_at")] DateTime? BeginAt,
    [property: JsonPropertyName("end_at")] DateTime? EndAt,
    [property: JsonPropertyName("winner_id")] long? WinnerId,
    [property: JsonPropertyName("league")] PandaScoreNamedEntity? League,
    [property: JsonPropertyName("serie")] PandaScoreSerie? Serie,
    [property: JsonPropertyName("tournament")] PandaScoreNamedEntity? Tournament,
    [property: JsonPropertyName("opponents")] List<PandaScoreOpponentSlot>? Opponents,
    [property: JsonPropertyName("results")] List<PandaScoreResult>? Results);

internal sealed record PandaScoreNamedEntity(
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("name")] string? Name);

internal sealed record PandaScoreSerie(
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("full_name")] string? FullName);

internal sealed record PandaScoreOpponentSlot(
    [property: JsonPropertyName("opponent")] PandaScoreOpponent? Opponent,
    [property: JsonPropertyName("type")] string? Type);

internal sealed record PandaScoreOpponent(
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("name")] string? Name);

internal sealed record PandaScoreResult(
    [property: JsonPropertyName("team_id")] long? TeamId,
    [property: JsonPropertyName("score")] int Score);

internal sealed record PandaScoreScopeEntity(
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("slug")] string? Slug,
    [property: JsonPropertyName("full_name")] string? FullName,
    [property: JsonPropertyName("league")] PandaScoreNamedEntity? League,
    [property: JsonPropertyName("serie")] PandaScoreSerie? Serie);
