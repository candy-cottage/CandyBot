using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Services.BotServices;
using Milimoe.FunGame.Core.Api.Transmittal;
using Milimoe.FunGame.Core.Library.Constant;

namespace CandyCottage.CandyBot.Services;

public sealed class ForzaSheetImporter(
    ILogger<ForzaSheetImporter> logger,
    SheetService sheetService,
    SQLHelper sql,
    ForzaService forzaService,
    ISheetParseDiagnostic diagnostic) : IForzaSheetImporter
{
    public const string DefaultVehicleSheetFileId = "DSXRieHNsZXpFZmZ4";

    public async Task<ForzaSheetSyncReport> SyncAsync(string fileId, CancellationToken cancellationToken = default)
    {
        Dictionary<string, List<List<string>>> sheets = await sheetService.GetSheetDataAsync(fileId);
        return await SyncLoadedSheetsAsync(sheets, cancellationToken);
    }

    internal async Task<ForzaSheetSyncReport> SyncLoadedSheetsAsync(
        Dictionary<string, List<List<string>>> sheets,
        CancellationToken cancellationToken = default)
    {
        List<VehicleRequest> vehicles = [];
        List<VehicleSheetSkippedRow> skippedRows = [];
        List<string> parsedSheetNames = [];
        List<string> skippedSheetNames = [];
        List<string> diagnostics = [];

        foreach ((string sheetName, List<List<string>> grid) in sheets)
        {
            if (IsSeasonalSheet(sheetName))
            {
                skippedSheetNames.Add(sheetName);
                continue;
            }

            if (!VehicleSheetParser.ContainsHeaderPairInGrid(grid))
            {
                skippedSheetNames.Add(sheetName);
                if (LooksLikeForzaClassSheet(sheetName))
                {
                    string diagnosis = await TryDiagnoseAsync(sheetName, grid, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(diagnosis)) diagnostics.Add($"{sheetName}: {diagnosis}");
                }
                continue;
            }

            VehicleSheetParseResult result = VehicleSheetParser.Parse(sheetName, grid);
            if (result.Vehicles.Count > 0)
            {
                parsedSheetNames.Add(sheetName);
                vehicles.AddRange(result.Vehicles);
            }
            else
            {
                skippedSheetNames.Add(sheetName);
                if (LooksLikeForzaClassSheet(sheetName))
                {
                    string diagnosis = await TryDiagnoseAsync(sheetName, grid, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(diagnosis)) diagnostics.Add($"{sheetName}: {diagnosis}");
                }
            }
            skippedRows.AddRange(result.SkippedRows);
        }

        int previousCount = CountVehicles();
        if (vehicles.Count == 0)
        {
            return new(false, previousCount, 0, 0, parsedSheetNames, skippedSheetNames, skippedRows, diagnostics, "解析结果为 0，已拒绝覆盖 Vehicles 旧数据。");
        }

        try
        {
            sql.NewTransaction();
            sql.Execute("DELETE FROM Vehicles");
            // SQLHelper.Success 仅在受影响行数 > 0 时为 true：对空表执行 DELETE 会得到 0 行、被判为 Fail。
            // 真正的执行失败只会把 Result 置为 SQLError（抛了异常），因此这里以 SQLError 作为失败判据。
            if (sql.Result == SQLResult.SQLError)
            {
                sql.Rollback();
                return new(false, previousCount, vehicles.Count, 0, parsedSheetNames, skippedSheetNames, skippedRows, diagnostics, "清空 Vehicles 失败，旧数据已保留。");
            }

            int inserted = 0;
            foreach (VehicleRequest vehicle in vehicles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!forzaService.InsertVehicle(vehicle, out string error))
                {
                    sql.Rollback();
                    return new(false, previousCount, vehicles.Count, inserted, parsedSheetNames, skippedSheetNames, skippedRows, diagnostics, $"写入车辆失败：{error}");
                }
                inserted++;
            }

            sql.Commit();
            return new(true, previousCount, vehicles.Count, inserted, parsedSheetNames, skippedSheetNames, skippedRows, diagnostics, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "同步 Forza 车辆表失败");
            sql.Rollback();
            return new(false, previousCount, vehicles.Count, 0, parsedSheetNames, skippedSheetNames, skippedRows, diagnostics, $"同步异常：{ex.Message}");
        }
        finally
        {
            sql.Rollback();
        }
    }

    private int CountVehicles()
    {
        System.Data.DataRow? row = sql.ExecuteDataRow("SELECT COUNT(*) FROM Vehicles");
        return sql.Success && row is not null ? Convert.ToInt32(row[0]) : 0;
    }

    private async Task<string> TryDiagnoseAsync(string sheetName, IReadOnlyList<IReadOnlyList<string>> grid, CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<IReadOnlyList<string>> sample = grid.Take(10).ToArray();
            return await diagnostic.DiagnoseAsync(sheetName, sample, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Forza 表格解析诊断失败: {SheetName}", sheetName);
            return "";
        }
    }

    private static bool IsSeasonalSheet(string sheetName)
        => sheetName.Contains("季节赛", StringComparison.Ordinal) || sheetName.Contains("每周", StringComparison.Ordinal);

    private static bool LooksLikeForzaClassSheet(string sheetName)
        => sheetName.StartsWith("R-", StringComparison.OrdinalIgnoreCase) ||
           sheetName.StartsWith("S2-", StringComparison.OrdinalIgnoreCase) ||
           sheetName.StartsWith("S1-", StringComparison.OrdinalIgnoreCase) ||
           sheetName.StartsWith("A-", StringComparison.OrdinalIgnoreCase) ||
           sheetName.StartsWith("B-", StringComparison.OrdinalIgnoreCase) ||
           sheetName.StartsWith("C-", StringComparison.OrdinalIgnoreCase);
}
