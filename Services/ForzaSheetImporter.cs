using System.Collections.Concurrent;
using CandyCottage.CandyBot.Interfaces;
using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Services.BotServices;
using CandyCottage.CandyBot.Utilities;
using Milimoe.FunGame.Core.Api.Transmittal;

namespace CandyCottage.CandyBot.Services;

public sealed class ForzaSheetImporter(
    ILogger<ForzaSheetImporter> logger,
    SheetService sheetService,
    SQLHelper sql,
    ForzaSyncService forzaSyncService,
    ISheetParseDiagnostic diagnostic) : IForzaSheetImporter
{
    /// <summary>
    /// 按来源（fileId）隔离的串行锁字典。同一 fileId 的并发同步请求按顺序执行；
    /// 不同 fileId 之间互不阻塞。<br/>
    /// 防御目的：避免同一来源的计划任务与手动触发在 Reconcile 之前并发抓取/解析，
    /// DB 层的 FOR UPDATE 行锁是额外的写保护层。
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _sourceLocks = new();

    public async Task<ForzaSheetSyncReport> SyncAsync(string fileId, long actorUserId = 0, CancellationToken cancellationToken = default)
    {
        return await RunSerializedAsync(fileId, async () =>
        {
            Dictionary<string, List<List<string>>> sheets = await sheetService.GetSheetDataAsync(fileId);
            return await SyncLoadedSheetsAsync(sheets, fileId, actorUserId, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// 对指定 <paramref name="fileId"/> 串行执行 <paramref name="body"/>：
    /// 同一 fileId 的请求排队等待，不同 fileId 可并发。
    /// 单元测试可直接调用此方法验证串行化行为。
    /// </summary>
    internal static async Task<T> RunSerializedAsync<T>(
        string fileId,
        Func<Task<T>> body,
        CancellationToken cancellationToken)
    {
        SemaphoreSlim sem = _sourceLocks.GetOrAdd(fileId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(cancellationToken);
        try
        {
            return await body();
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>
    /// 解析表格数据并委托 <see cref="ForzaSyncService.Reconcile"/> 进行按来源协调写库。
    /// 不再直接操作 Vehicles 表，不做全表清空（修复多 DocsID 互相覆盖数据丢失 bug）。
    /// </summary>
    internal async Task<ForzaSheetSyncReport> SyncLoadedSheetsAsync(
        Dictionary<string, List<List<string>>> sheets,
        string fileId = "",
        long actorUserId = 0,
        CancellationToken cancellationToken = default)
    {
        List<VehicleTuneRequest> tunes = [];
        List<VehicleSheetSkippedRow> skippedRows = [];
        List<string> parsedClassSheets = [];
        List<string> skippedClassSheets = [];
        List<string> emptyClassSheets = [];
        List<string> skippedSheetNames = [];
        List<string> diagnostics = [];
        int diagnosticFailureCount = 0;

        foreach ((string sheetName, List<List<string>> grid) in sheets)
        {
            if (IsSeasonalSheet(sheetName))
            {
                skippedSheetNames.Add(sheetName);
                continue;
            }

            // 没有表头对 → 跳过；若是班级 sheet 则尝试诊断（可能是格式问题）
            if (!VehicleSheetParser.ContainsHeaderPairInGrid(grid))
            {
                skippedSheetNames.Add(sheetName);
                if (LooksLikeForzaClassSheet(sheetName))
                {
                    skippedClassSheets.Add(sheetName);
                    string diagnosis = await TryDiagnoseAsync(sheetName, grid, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(diagnosis))
                    {
                        diagnostics.Add($"{sheetName}: {diagnosis}");
                        diagnosticFailureCount++;
                    }
                }
                continue;
            }

            VehicleSheetParseResult result = VehicleSheetParser.Parse(sheetName, grid);
            skippedRows.AddRange(result.SkippedRows);

            if (result.Vehicles.Count > 0)
            {
                if (LooksLikeForzaClassSheet(sheetName))
                    parsedClassSheets.Add(sheetName);

                // VehicleRequest → VehicleTuneRequest 映射
                foreach (VehicleRequest v in result.Vehicles)
                {
                    tunes.Add(MapToTuneRequest(v, fileId, sheetName));
                }
            }
            else
            {
                skippedSheetNames.Add(sheetName);
                if (LooksLikeForzaClassSheet(sheetName))
                {
                    emptyClassSheets.Add(sheetName);
                    string diagnosis = await TryDiagnoseAsync(sheetName, grid, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(diagnosis))
                    {
                        diagnostics.Add($"{sheetName}: {diagnosis}");
                        diagnosticFailureCount++;
                    }
                }
            }
        }

        int previousCount = CountVehicles();

        // 零解析守卫：拒绝协调，不调用 Reconcile
        if (tunes.Count == 0)
        {
            return new(false, previousCount, 0, 0, parsedClassSheets, skippedSheetNames, skippedRows, diagnostics,
                "解析结果为 0，已拒绝覆盖旧数据。", ActorUserId: actorUserId);
        }

        bool complete = ForzaTuneIdentity.IsCompleteSnapshot(
            parsedClassSheets, skippedClassSheets, emptyClassSheets, diagnosticFailureCount);

        ForzaSyncReport rec = forzaSyncService.Reconcile(tunes, fileId, complete, actorUserId);

        if (rec.ErrorMessage is not null)
        {
            logger.LogError("Forza 同步协调失败（fileId={FileId}）：{Error}", fileId, rec.ErrorMessage);
            return new(false, previousCount, tunes.Count, 0,
                parsedClassSheets, skippedSheetNames, skippedRows, diagnostics, rec.ErrorMessage,
                ParsedTuneCount: rec.ParsedCount,
                ActorUserId: actorUserId);
        }

        return new(true, previousCount, tunes.Count, rec.Inserted,
            parsedClassSheets, skippedSheetNames, skippedRows, diagnostics, null,
            ParsedTuneCount: rec.ParsedCount,
            UpdatedInPlaceCount: rec.UpdatedInPlace,
            CachedCount: rec.Cached,
            ExpiredCount: rec.Expired,
            DeletedTuneCount: rec.Deleted,
            ActorUserId: actorUserId);
    }

    /// <summary>
    /// 将 VehicleSheetParser 解析出的 VehicleRequest 映射为 VehicleTuneRequest，
    /// 供 ForzaSyncService.Reconcile 使用。来源字段（SourceFileId/SourceSheet 等）
    /// 在映射阶段填入；VehicleId 由 Reconcile Phase 1（EnsureVehicleModel）回填。
    /// </summary>
    private static VehicleTuneRequest MapToTuneRequest(VehicleRequest v, string fileId, string sheetName) => new()
    {
        Manufacturer = v.Manufacturer,
        Model = v.Model,
        Year = v.Year,
        Class = v.Class,
        MatchType = v.MatchType,
        Drivetrain = v.Drivetrain,
        PerformancePreference = v.Attribute,   // VehicleRequest.Attribute → PerformancePreference
        TireCompound = v.TireCompound,
        SurfaceCondition = v.SurfaceCondition,
        Strength = v.Strength,
        TuneRaw = v.Tune,                      // VehicleRequest.Tune → TuneRaw
        Remarks = v.Remarks,
        LapTime = v.LapTime,
        ExtData = v.ExtData,
        // 来源元数据
        SourceFileId = fileId,
        SourceSheet = sheetName,
        SourceRow = null,
        SourceDisplayName = null,
        // 同步行默认值（Reconcile 会覆写 TuneStatus/IsManual/LastUpdatedUserId）
        IsManual = false,
        TuneStatus = null,
        VehicleId = null,
    };

    private int CountVehicles()
    {
        System.Data.DataRow? row = sql.ExecuteDataRow("SELECT COUNT(*) FROM Vehicles");
        return sql.Success && row is not null ? Convert.ToInt32(row[0]) : 0;
    }

    private async Task<string> TryDiagnoseAsync(string sheetName, IReadOnlyList<IReadOnlyList<string>> grid, CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<IReadOnlyList<string>> sample = [.. grid.Take(10)];
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
