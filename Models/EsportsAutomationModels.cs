namespace CandyCottage.CandyBot.Models;

public sealed record ExternalEsportsMatch(
    string ExternalId,
    string EventExternalId,
    string EventName,
    DateTime EventStartTime,
    DateTime EventEndTime,
    string Team1Name,
    string Team2Name,
    DateTime StartTime,
    string Stage,
    decimal? Team1WinProbability = null,
    decimal? Team1WinOdds = null,
    decimal? Team2WinOdds = null);

public sealed record ExternalEsportsResult(
    string ExternalId,
    string ResultText,
    int Winner);

public sealed record ExternalEsportsScope(
    string Type,
    string Name,
    string Value,
    long? Id = null,
    string? ParentName = null);

public sealed record EsportsScheduleSyncReport(
    int Fetched,
    int EventsCreated,
    int EventsUpdated,
    int MatchesCreated,
    int MatchesUpdated,
    int Failed,
    IReadOnlyList<string> Errors);

public sealed record EsportsResultSyncReport(
    int Checked,
    int PendingSettlementCreated,
    int RequiresManualSettlement,
    int Failed,
    IReadOnlyList<string> Errors);
