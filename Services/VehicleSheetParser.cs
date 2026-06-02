using CandyCottage.CandyBot.Models;

namespace CandyCottage.CandyBot.Services;

/// <summary>
/// 纯函数式的 Forza 车辆表解析器：将腾讯文档导出的一张工作表（行 → 单元格）转换为 <see cref="VehicleRequest"/> 列表。
/// 不做任何 I/O，便于直接对真实表格快照做回归测试。相较旧版导入器修正了三个会丢数据的缺陷：
/// 1. 列映射保留首个匹配表头，避免右侧 “Purist专区” 同名表头（制造商/型号）覆盖主表列；
/// 2. 空型号 + 调校/备注/圈速/强度 视为纵向合并的续行（同一辆车的第 2/3 套调校），继承上一辆车并各自成行；
/// 3. 缺少 NOT NULL 必填列（等级/比赛类型/制造商/型号）的行计入 SkippedRows，绝不抛异常中断整批。
/// </summary>
internal static class VehicleSheetParser
{
    public static VehicleSheetParseResult Parse(string sheetName, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        List<VehicleRequest> vehicles = [];
        List<VehicleSheetSkippedRow> skipped = [];
        if (rows.Count == 0) return new(vehicles, skipped);

        string? vehicleClass = ExtractClassFromSheetName(sheetName);
        string? defaultMatchType = ExtractMatchTypeFromSheetName(sheetName);

        int i = 0;
        while (i < rows.Count)
        {
            IReadOnlyList<string> row = rows[i];
            if (!ContainsHeaderPair(row))
            {
                i++;
                continue;
            }

            Dictionary<string, int> columns = BuildColumnMap(row);
            int colManufacturer = GetColumnIndex(columns, "制造商");
            int colModel = GetColumnIndex(columns, "型号");
            int colYear = GetColumnIndex(columns, "年份");
            int colDrivetrain = GetColumnIndex(columns, "传动");
            int colAttribute = GetColumnIndex(columns, "属性");
            int colTire = GetColumnIndex(columns, "胎面");
            int colSurface = GetColumnIndex(columns, "干/湿");
            int colStrength = GetColumnIndex(columns, "强度");
            int colTune = GetColumnIndex(columns, "调校");
            int colRemarks = GetColumnIndex(columns, "备注");
            int colLapTime = GetColumnIndex(columns, "圈速测试");

            i++;
            string? currentMatchType = defaultMatchType;
            string? lastManufacturer = null;
            string? lastModel = null;

            while (i < rows.Count)
            {
                IReadOnlyList<string> dataRow = rows[i];
                if (ContainsHeaderPair(dataRow)) break;

                string firstCell = GetCellValue(dataRow, 0) ?? "";
                if (firstCell.Contains("比赛类型：", StringComparison.Ordinal))
                {
                    currentMatchType = ExtractMatchTypeFromText(firstCell) ?? currentMatchType;
                    i++;
                    continue;
                }

                string? manufacturer = GetCellValue(dataRow, colManufacturer);
                string? model = GetCellValue(dataRow, colModel);
                string? tune = GetCellValue(dataRow, colTune);
                string? remarks = GetCellValue(dataRow, colRemarks);
                string? lapTime = GetCellValue(dataRow, colLapTime);
                string? strength = GetCellValue(dataRow, colStrength);

                if (string.IsNullOrWhiteSpace(manufacturer)) manufacturer = lastManufacturer;
                bool hasContinuationData = HasAnyValue(tune, remarks, lapTime, strength);
                if (string.IsNullOrWhiteSpace(model) && hasContinuationData)
                {
                    model = lastModel;
                }

                VehicleRequest vehicle = new()
                {
                    Class = vehicleClass,
                    MatchType = currentMatchType,
                    Manufacturer = Clean(manufacturer),
                    Model = Clean(model),
                    Year = GetCellValue(dataRow, colYear),
                    Drivetrain = GetCellValue(dataRow, colDrivetrain),
                    Attribute = GetCellValue(dataRow, colAttribute),
                    TireCompound = GetCellValue(dataRow, colTire),
                    SurfaceCondition = GetCellValue(dataRow, colSurface),
                    Strength = strength,
                    Tune = tune,
                    Remarks = remarks,
                    LapTime = lapTime,
                };

                string? reason = GetRequiredFieldFailure(vehicle);
                if (reason is not null)
                {
                    if (HasAnyValue([.. dataRow]))
                        skipped.Add(new(sheetName, i + 1, reason, [.. dataRow]));
                    i++;
                    continue;
                }

                vehicles.Add(vehicle);
                lastManufacturer = vehicle.Manufacturer;
                lastModel = vehicle.Model;
                i++;
            }
        }

        return new(vehicles, skipped);
    }

    internal static bool ContainsHeaderPair(IReadOnlyList<string> row)
        => row.Any(c => c.Contains("制造商", StringComparison.OrdinalIgnoreCase)) &&
           row.Any(c => c.Contains("型号", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 构建 表头单元格文本 → 列下标 的映射。Fix #1：保留首个匹配的表头，绝不被后出现的同名表头覆盖。
    /// 主表与右侧 Purist专区 共享同一物理行时，制造商/型号会出现两次；首匹配优先保证指向主表列。
    /// </summary>
    private static Dictionary<string, int> BuildColumnMap(IReadOnlyList<string> headerRow)
    {
        Dictionary<string, int> map = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headerRow.Count; i++)
        {
            string cell = headerRow[i].Trim();
            if (cell.Length > 0 && !map.ContainsKey(cell))
            {
                map[cell] = i;
            }
        }
        return map;
    }

    private static int GetColumnIndex(Dictionary<string, int> map, string target)
    {
        foreach ((string key, int value) in map)
        {
            if (key.Contains(target, StringComparison.OrdinalIgnoreCase)) return value;
        }
        return -1;
    }

    private static string? ExtractClassFromSheetName(string sheetName)
        => sheetName.StartsWith("R-", StringComparison.OrdinalIgnoreCase) ? "R" :
           sheetName.StartsWith("S2-", StringComparison.OrdinalIgnoreCase) ? "S2" :
           sheetName.StartsWith("S1-", StringComparison.OrdinalIgnoreCase) ? "S1" :
           sheetName.StartsWith("A-", StringComparison.OrdinalIgnoreCase) ? "A" :
           sheetName.StartsWith("B-", StringComparison.OrdinalIgnoreCase) ? "B" :
           sheetName.StartsWith("C-", StringComparison.OrdinalIgnoreCase) ? "C" :
           null;

    private static string? ExtractMatchTypeFromSheetName(string sheetName)
        => sheetName.Contains("公路", StringComparison.Ordinal) ? "公路" :
           sheetName.Contains("泥地", StringComparison.Ordinal) ? "拉力" :
           null;

    private static string? ExtractMatchTypeFromText(string text)
        => text.Contains("公路", StringComparison.Ordinal) ? "公路" :
           text.Contains("拉力", StringComparison.Ordinal) || text.Contains("泥地", StringComparison.Ordinal) ? "拉力" :
           text.Contains("越野", StringComparison.Ordinal) ? "越野" :
           null;

    private static string? GetRequiredFieldFailure(VehicleRequest vehicle)
    {
        if (string.IsNullOrWhiteSpace(vehicle.Class)) return "缺少等级";
        if (string.IsNullOrWhiteSpace(vehicle.MatchType)) return "缺少比赛类型";
        if (string.IsNullOrWhiteSpace(vehicle.Manufacturer)) return "缺少制造商";
        if (string.IsNullOrWhiteSpace(vehicle.Model)) return "缺少型号";
        return null;
    }

    private static string? GetCellValue(IReadOnlyList<string> row, int colIndex)
    {
        if (colIndex < 0 || colIndex >= row.Count) return null;
        return Clean(row[colIndex]);
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool HasAnyValue(params string?[] values)
        => values.Any(v => !string.IsNullOrWhiteSpace(v));
}
