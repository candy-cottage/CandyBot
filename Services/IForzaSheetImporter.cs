using CandyCottage.CandyBot.Models;

namespace CandyCottage.CandyBot.Services;

public interface IForzaSheetImporter
{
    Task<ForzaSheetSyncReport> SyncAsync(string fileId, CancellationToken cancellationToken = default);
}
