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
    /// <summary>本次协调新增的调校行数（旧字段 InsertedVehicleCount 的语义修正版）。</summary>
    int InsertedTuneCount,
    IReadOnlyList<string> ParsedSheetNames,
    IReadOnlyList<string> SkippedSheetNames,
    IReadOnlyList<VehicleSheetSkippedRow> SkippedRows,
    IReadOnlyList<string> Diagnostics,
    string? ErrorMessage,
    /// <summary>协调引擎解析到的调校行总数。</summary>
    int ParsedTuneCount = 0,
    int UpdatedInPlaceCount = 0,
    int CachedCount = 0,
    int ExpiredCount = 0,
    int DeletedTuneCount = 0);
