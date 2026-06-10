namespace CandyCottage.CandyBot.Interfaces;

public interface ISheetParseDiagnostic
{
    Task<string> DiagnoseAsync(string sheetName, IReadOnlyList<IReadOnlyList<string>> sampleRows, CancellationToken cancellationToken = default);
}
