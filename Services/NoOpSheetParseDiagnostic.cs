namespace CandyCottage.CandyBot.Services;

public sealed class NoOpSheetParseDiagnostic : ISheetParseDiagnostic
{
    public Task<string> DiagnoseAsync(string sheetName, IReadOnlyList<IReadOnlyList<string>> sampleRows, CancellationToken cancellationToken = default)
        => Task.FromResult("");
}
