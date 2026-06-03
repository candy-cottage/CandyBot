using CandyCottage.CandyBot.Models;

namespace CandyCottage.CandyBot.Services.Esports;

public interface IEsportsDataProvider
{
    Task<IReadOnlyList<ExternalEsportsMatch>> GetUpcomingMatchesAsync(string gameSlug, DateTime since, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ExternalEsportsResult>> GetFinishedResultsAsync(IReadOnlyCollection<string> externalIds, CancellationToken cancellationToken = default);
}
