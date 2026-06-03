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
    ILogger<PandaScoreProvider> logger) : IEsportsDataProvider
{
    public async Task<IReadOnlyList<ExternalEsportsMatch>> GetUpcomingMatchesAsync(string gameSlug, DateTime since, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            logger.LogWarning("PandaScore API Key 未配置，跳过赛程同步");
            return [];
        }

        string cacheKey = $"pandascore:upcoming:{gameSlug}:{since:yyyyMMddHH}";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<ExternalEsportsMatch>? cached) && cached is not null)
        {
            return cached;
        }

        string url = $"{config.BaseUrl.TrimEnd('/')}/{gameSlug}/matches?filter[status]=not_started&range[scheduled_at]={Uri.EscapeDataString(since.ToString("O"))},{Uri.EscapeDataString(since.AddDays(30).ToString("O"))}&sort=scheduled_at&page[size]=100";
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);

        string json = await SendAsync(request, cancellationToken);
        List<ExternalEsportsMatch> result = PandaScoreMapper.MapMatches(json);
        cache.Set(cacheKey, result, TimeSpan.FromMinutes(5));
        return result;
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

    private async Task<string> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
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

    private static ExternalEsportsMatch MapMatch(PandaScoreMatch source)
    {
        PandaScoreOpponent? team1 = source.Opponents.ElementAtOrDefault(0)?.Opponent;
        PandaScoreOpponent? team2 = source.Opponents.ElementAtOrDefault(1)?.Opponent;
        DateTime start = source.ScheduledAt ?? source.BeginAt ?? DateTime.UtcNow;
        string league = source.League?.Name ?? "";
        string serie = source.Serie?.FullName ?? source.Serie?.Name ?? "";
        string eventName = string.Join(" ", new[] { league, serie }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
        if (string.IsNullOrWhiteSpace(eventName)) eventName = source.Name;

        return new ExternalEsportsMatch(
            ExternalId: source.Id.ToString(),
            EventExternalId: $"{source.League?.Id}:{source.Serie?.Id}:{source.Tournament?.Id}",
            EventName: eventName,
            EventStartTime: start.Date,
            EventEndTime: start.Date.AddDays(1),
            Team1Name: team1?.Name ?? "",
            Team2Name: team2?.Name ?? "",
            StartTime: DateTime.SpecifyKind(start, DateTimeKind.Utc),
            Stage: source.Tournament?.Name ?? "",
            Team1WinProbability: null,
            Team1WinOdds: null,
            Team2WinOdds: null);
    }

    private static ExternalEsportsResult MapResult(PandaScoreMatch source)
    {
        long? team1Id = source.Opponents.ElementAtOrDefault(0)?.Opponent?.Id;
        long? team2Id = source.Opponents.ElementAtOrDefault(1)?.Opponent?.Id;
        int winner = source.WinnerId == team1Id ? 1 : source.WinnerId == team2Id ? 2 : 0;
        int score1 = source.Results.FirstOrDefault(r => r.TeamId == team1Id)?.Score ?? 0;
        int score2 = source.Results.FirstOrDefault(r => r.TeamId == team2Id)?.Score ?? 0;
        return new(source.Id.ToString(), $"{score1}:{score2}", winner);
    }
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
    [property: JsonPropertyName("opponents")] List<PandaScoreOpponentSlot> Opponents,
    [property: JsonPropertyName("results")] List<PandaScoreResult> Results);

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
