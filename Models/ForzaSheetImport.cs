namespace CandyCottage.CandyBot.Models;

public sealed record VehicleSheetSkippedRow(
    string SheetName,
    int RowNumber,
    string Reason,
    IReadOnlyList<string> Cells);

public sealed record VehicleSheetParseResult(
    IReadOnlyList<VehicleRequest> Vehicles,
    IReadOnlyList<VehicleSheetSkippedRow> SkippedRows);

public sealed record ForzaSheetSyncReport(
    bool Success,
    int PreviousVehicleCount,
    int ParsedVehicleCount,
    int InsertedVehicleCount,
    IReadOnlyList<string> ParsedSheetNames,
    IReadOnlyList<string> SkippedSheetNames,
    IReadOnlyList<VehicleSheetSkippedRow> SkippedRows,
    IReadOnlyList<string> Diagnostics,
    string? ErrorMessage);
