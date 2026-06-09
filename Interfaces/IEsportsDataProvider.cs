using CandyCottage.CandyBot.Models;

namespace CandyCottage.CandyBot.Interfaces;

public interface IEsportsDataProvider
{
    Task<IReadOnlyList<ExternalEsportsMatch>> GetUpcomingMatchesAsync(string gameSlug, DateTime since, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ExternalEsportsResult>> GetFinishedResultsAsync(IReadOnlyCollection<string> externalIds, CancellationToken cancellationToken = default);
}

public interface IEsportsScopeSearchProvider
{
    Task<IReadOnlyList<ExternalEsportsScope>> SearchScopesAsync(string gameSlug, string keyword, CancellationToken cancellationToken = default);
}
