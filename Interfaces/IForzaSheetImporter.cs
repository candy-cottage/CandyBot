using CandyCottage.CandyBot.Models;

namespace CandyCottage.CandyBot.Interfaces;

public interface IForzaSheetImporter
{
    Task<ForzaSheetSyncReport> SyncAsync(string fileId, CancellationToken cancellationToken = default);
}
