namespace CandyCottage.CandyBot.Services;

public interface ISheetParseDiagnostic
{
    Task<string> DiagnoseAsync(string sheetName, IReadOnlyList<IReadOnlyList<string>> sampleRows, CancellationToken cancellationToken = default);
}
