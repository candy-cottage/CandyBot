using System.Data;
using System.Text;
using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Utilities;
using Milimoe.FunGame.Core.Api.Transmittal;

namespace CandyCottage.CandyBot.Services
{
    public class MapService(ILogger<MapService> logger, SQLHelper sql)
    {
        private const int DefaultPageSize = 12;

        // ==================== 查询方法 ====================

        public (bool success, string message, DataTable? data, int totalPages) GetMapDataTable(string whereClause, int page, int pageSize = DefaultPageSize)
        {
            if (page < 1) page = 1;
            int offset = (page - 1) * pageSize;

            string query = $@"
                SELECT
                    Id, NameZh, NameEn, Aliases, LengthKm, RouteType, Surface,
                    RaceCategory, Characteristic, Difficulty, Elevation, TurnCount,
                    RoadWidth, AvgTimeSec, WorldRecord, WrVehicle, WrTuning,
                    CarClassBest, CarTypeBest, Region, SeasonBest, WeatherDefault,
                    NightMode, Dlc, IsReverse, ImageUrl, MinimapUrl, Notes
                FROM Maps
                {whereClause}
                ORDER BY NameZh
                LIMIT {offset}, {pageSize}";

            sql.ClearParametersAfterExecute = false;
            sql.ExecuteDataSet(query);
            sql.ClearParametersAfterExecute = true;

            if (sql.DataSet is null || sql.DataSet.Tables.Count == 0 || sql.DataSet.Tables[0].Rows.Count == 0)
                return (false, whereClause.Contains("LIKE") ? "未找到相关赛道。" : "未找到符合条件的赛道。", null, 0);

            DataTable dt = sql.DataSet.Tables[0];

            string countQuery = $"SELECT COUNT(*) FROM Maps {whereClause}";
            DataRow? dr = sql.ExecuteDataRow(countQuery);
            int totalCount = 0;
            if (sql.Success && dr != null) _ = int.TryParse(dr[0].ToString(), out totalCount);

            int totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
            return (true, "success", dt, totalPages);
        }

        public (bool success, string message, DataTable? data, int totalPages) GetMapById(int id)
        {
            sql.Parameters["@id"] = id;
            return GetMapDataTable("WHERE Id = @id", 1, 1);
        }

        /// <summary>
        /// 按关键字搜索赛道：匹配中文名、英文名、别名（JSON_SEARCH）。
        /// </summary>
        public (bool success, string message, DataTable? data, int totalPages) SearchMaps(string keyword, int page)
        {
            if (string.IsNullOrWhiteSpace(keyword))
                return (false, "请提供赛道名称关键字。", null, 0);

            string escapedKeyword = EscapeLikeWildcards(keyword);
            sql.Parameters["@search"] = $"%{escapedKeyword}%";
            sql.Parameters["@aliasSearch"] = $"%{escapedKeyword}%";

            string whereClause = @"
                WHERE NameZh LIKE @search
                   OR NameEn LIKE @search
                   OR JSON_SEARCH(Aliases, 'one', @aliasSearch) IS NOT NULL";

            return GetMapDataTable(whereClause, page);
        }

        // ==================== 写操作 ====================

        public bool AddMap(MapRequest request, out string error)
        {
            error = "";

            if (string.IsNullOrEmpty(request.NameZh))
            {
                error = "中文名不能为空。";
                return false;
            }
            if (string.IsNullOrEmpty(request.RouteType))
            {
                error = "路线类型不能为空。";
                return false;
            }

            try
            {
                sql.NewTransaction();
                if (!InsertMap(request, out error))
                    return false;
                sql.Commit();
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AddMap error: {Message}", ex.Message);
                error = $"新增赛道异常：{ex.Message}";
                return false;
            }
            finally
            {
                sql.Rollback();
            }
        }

        internal bool InsertMap(MapRequest request, out string error)
        {
            error = "";
            try
            {
                List<string> columns = [];
                List<string> values = [];
                AddField(columns, values, "NameZh", request.NameZh);
                AddField(columns, values, "NameEn", request.NameEn);
                AddField(columns, values, "Aliases", request.Aliases);
                AddField(columns, values, "LengthKm", request.LengthKm);
                AddField(columns, values, "RouteType", request.RouteType);
                AddField(columns, values, "Surface", request.Surface);
                AddField(columns, values, "RaceCategory", request.RaceCategory);
                AddField(columns, values, "Characteristic", request.Characteristic);
                AddField(columns, values, "Difficulty", request.Difficulty);
                AddField(columns, values, "Elevation", request.Elevation);
                AddField(columns, values, "TurnCount", request.TurnCount);
                AddField(columns, values, "RoadWidth", request.RoadWidth);
                AddField(columns, values, "AvgTimeSec", request.AvgTimeSec);
                AddField(columns, values, "WorldRecord", request.WorldRecord);
                AddField(columns, values, "WrVehicle", request.WrVehicle);
                AddField(columns, values, "WrTuning", request.WrTuning);
                AddField(columns, values, "CarClassBest", request.CarClassBest);
                AddField(columns, values, "CarTypeBest", request.CarTypeBest);
                AddField(columns, values, "Region", request.Region);
                AddField(columns, values, "SeasonBest", request.SeasonBest);
                AddField(columns, values, "WeatherDefault", request.WeatherDefault);
                AddField(columns, values, "NightMode", request.NightMode);
                AddField(columns, values, "Dlc", request.Dlc);
                AddField(columns, values, "IsReverse", request.IsReverse);
                AddField(columns, values, "ImageUrl", request.ImageUrl);
                AddField(columns, values, "MinimapUrl", request.MinimapUrl);
                AddField(columns, values, "Notes", request.Notes);

                string insertSql = $"INSERT INTO Maps ({string.Join(", ", columns)}) VALUES ({string.Join(", ", values)})";
                sql.Execute(insertSql);
                if (!sql.Success)
                {
                    error = "新增赛道失败，请稍后重试。";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "InsertMap error: {Message}", ex.Message);
                error = $"新增赛道异常：{ex.Message}";
                return false;
            }
        }

        public bool UpdateMap(MapRequest request, out string error)
        {
            error = "";
            try
            {
                sql.NewTransaction();

                int mapId;
                if (request.Id.HasValue && request.Id.Value > 0)
                {
                    sql.Parameters["@id"] = request.Id.Value;
                    sql.ExecuteDataSetForUpdate("SELECT Id FROM Maps WHERE Id = @id");
                    mapId = request.Id.Value;
                }
                else
                {
                    error = "请提供赛道ID以定位赛道。";
                    return false;
                }

                if (!sql.Success || sql.DataSet.Tables[0].Rows.Count == 0)
                {
                    error = "未找到对应的赛道。请检查ID是否正确。";
                    return false;
                }

                List<string> setClauses = [];
                AddSetClause(setClauses, "NameZh", request.NameZh);
                AddSetClause(setClauses, "NameEn", request.NameEn);
                AddSetClause(setClauses, "Aliases", request.Aliases);
                AddSetClause(setClauses, "LengthKm", request.LengthKm);
                AddSetClause(setClauses, "RouteType", request.RouteType);
                AddSetClause(setClauses, "Surface", request.Surface);
                AddSetClause(setClauses, "RaceCategory", request.RaceCategory);
                AddSetClause(setClauses, "Characteristic", request.Characteristic);
                AddSetClause(setClauses, "Difficulty", request.Difficulty);
                AddSetClause(setClauses, "Elevation", request.Elevation);
                AddSetClause(setClauses, "TurnCount", request.TurnCount);
                AddSetClause(setClauses, "RoadWidth", request.RoadWidth);
                AddSetClause(setClauses, "AvgTimeSec", request.AvgTimeSec);
                AddSetClause(setClauses, "WorldRecord", request.WorldRecord);
                AddSetClause(setClauses, "WrVehicle", request.WrVehicle);
                AddSetClause(setClauses, "WrTuning", request.WrTuning);
                AddSetClause(setClauses, "CarClassBest", request.CarClassBest);
                AddSetClause(setClauses, "CarTypeBest", request.CarTypeBest);
                AddSetClause(setClauses, "Region", request.Region);
                AddSetClause(setClauses, "SeasonBest", request.SeasonBest);
                AddSetClause(setClauses, "WeatherDefault", request.WeatherDefault);
                AddSetClause(setClauses, "NightMode", request.NightMode);
                AddSetClause(setClauses, "Dlc", request.Dlc);
                AddSetClause(setClauses, "IsReverse", request.IsReverse);
                AddSetClause(setClauses, "ImageUrl", request.ImageUrl);
                AddSetClause(setClauses, "MinimapUrl", request.MinimapUrl);
                AddSetClause(setClauses, "Notes", request.Notes);

                if (setClauses.Count == 0)
                {
                    error = "没有提供任何修改参数。";
                    return false;
                }

                string updateSql = $"UPDATE Maps SET {string.Join(", ", setClauses)} WHERE Id = @id";
                sql.Parameters["@id"] = mapId;
                sql.Execute(updateSql);
                if (!sql.Success)
                {
                    error = "更新赛道信息失败。";
                    return false;
                }

                sql.Commit();
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "UpdateMap error: {Message}", ex.Message);
                error = $"修改赛道异常：{ex.Message}";
                return false;
            }
            finally
            {
                sql.Rollback();
            }
        }

        public (bool success, string message, int deletedCount) DeleteMapsByIds(List<int> ids)
        {
            if (ids == null || ids.Count == 0)
                return (false, "未提供有效的赛道ID", 0);

            try
            {
                sql.NewTransaction();
                string inClause = string.Join(",", ids.Select((_, i) => $"@id{i}"));
                for (int i = 0; i < ids.Count; i++)
                    sql.Parameters[$"@id{i}"] = ids[i];

                string query = $"DELETE FROM Maps WHERE Id IN ({inClause})";
                sql.Execute(query);
                if (!sql.Success)
                    return (false, "删除失败，请稍后重试", 0);

                sql.Commit();
                return (true, $"成功删除 {ids.Count} 条赛道", ids.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "DeleteMapsByIds error");
                return (false, $"删除异常：{ex.Message}", 0);
            }
            finally
            {
                sql.Rollback();
            }
        }

        public (bool success, string message, List<MapMatch>? matches) FindMapsByCriteria(MapRequest request)
        {
            List<MapMatch> matches = [];

            try
            {
                sql.Parameters.Clear();
                List<string> whereClauses = [];
                AddWhereClause(whereClauses, "NameZh", request.NameZh);
                AddWhereClause(whereClauses, "NameEn", request.NameEn);
                AddWhereClause(whereClauses, "RouteType", request.RouteType);
                AddWhereClause(whereClauses, "Surface", request.Surface);
                AddWhereClause(whereClauses, "RaceCategory", request.RaceCategory);
                AddWhereClause(whereClauses, "Characteristic", request.Characteristic);
                AddWhereClause(whereClauses, "Difficulty", request.Difficulty);
                AddWhereClause(whereClauses, "Region", request.Region);
                AddWhereClause(whereClauses, "Dlc", request.Dlc);

                string query = $@"
                    SELECT Id, NameZh, NameEn, LengthKm, RouteType, Surface, Characteristic
                    FROM Maps
                    {string.Join(" ", whereClauses)}";

                sql.ExecuteDataSet(query);
                if (!sql.Success || sql.DataSet.Tables[0].Rows.Count == 0)
                    return (false, "未找到匹配的赛道", null);

                DataTable dt = sql.DataSet.Tables[0];
                foreach (DataRow row in dt.Rows)
                {
                    string display = $"{row["NameZh"]} ({row["NameEn"]}) [{row["LengthKm"]}km · {row["RouteType"]}/{row["Surface"]} · {row["Characteristic"]}]";
                    matches.Add(new MapMatch
                    {
                        Id = Convert.ToInt32(row["Id"]),
                        DisplayName = display
                    });
                }

                return (true, $"找到 {matches.Count} 条匹配的赛道", matches);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "FindMapsByCriteria error");
                return (false, $"查找异常：{ex.Message}", null);
            }
        }

        // ==================== 呈现方法 ====================

        internal static string RenderMapsAsCards(DataTable dt)
        {
            StringBuilder sb = new();
            for (int i = 0; i < dt.Rows.Count; i++)
            {
                DataRow row = dt.Rows[i];
                string Cell(string col) => dt.Columns.Contains(col) ? (row[col]?.ToString() ?? "").Trim() : "";

                // 标题行：中文名 (英文名) · 路线类型/路面 · 赛道特性
                string title = $"**{Cell("NameZh")}**";
                if (Cell("NameEn") != "") title += $" ({Cell("NameEn")})";
                title += $" · {Cell("RouteType")}/{Cell("Surface")} · {Cell("Characteristic")}";
                if (Cell("IsReverse") == "1") title += " 🔄";
                if (Cell("NightMode") == "1") title += " 🌙";
                title += $"\r\n> ";
                sb.Append(title);

                // 长度 | 难度 | 海拔 | 弯道
                List<string> stats = [];
                if (Cell("LengthKm") != "") stats.Add($"全长 {Cell("LengthKm")} km");
                if (Cell("Difficulty") != "") stats.Add($"难度 {Cell("Difficulty")}/5");
                if (Cell("Elevation") != "") stats.Add(Cell("Elevation"));
                if (Cell("TurnCount") != "") stats.Add($"{Cell("TurnCount")} 弯");
                AppendLineIfAny(sb, string.Join(" | ", stats));

                // 赛事大类 | 道路宽度
                List<string> extra = [];
                if (Cell("RaceCategory") != "") extra.Add(Cell("RaceCategory"));
                if (Cell("RoadWidth") != "") extra.Add($"路宽: {Cell("RoadWidth")}");
                AppendLineIfAny(sb, string.Join(" · ", extra));

                // 推荐车辆等级 / 类型
                List<string> recommends = [];
                if (Cell("CarClassBest") != "") recommends.Add($"等级推荐: {Cell("CarClassBest")}");
                if (Cell("CarTypeBest") != "") recommends.Add($"车型推荐: {Cell("CarTypeBest")}");
                AppendLineIfAny(sb, string.Join(" | ", recommends));

                // 最佳赛季 | 默认天气
                List<string> env = [];
                if (Cell("SeasonBest") != "") env.Add($"最佳赛季: {Cell("SeasonBest")}");
                if (Cell("WeatherDefault") != "") env.Add($"天气: {Cell("WeatherDefault")}");
                AppendLineIfAny(sb, string.Join(" · ", env));

                // 区域 | DLC
                List<string> loc = [];
                if (Cell("Region") != "") loc.Add(Cell("Region"));
                if (Cell("Dlc") != "") loc.Add($"DLC: {Cell("Dlc")}");
                AppendLineIfAny(sb, string.Join(" · ", loc));

                // 世界纪录
                if (Cell("WorldRecord") != "")
                {
                    string wrLine = $"🏆 世界纪录: {Cell("WorldRecord")}";
                    if (Cell("WrVehicle") != "") wrLine += $" · {Cell("WrVehicle")}";
                    AppendLineIfAny(sb, wrLine);
                }

                // 备注
                AppendLineIfAny(sb, Prefix("备注：", Cell("Notes")));

                if (i < dt.Rows.Count - 1) sb.AppendLine("\r\n");
            }
            return sb.ToString();
        }

        // ==================== 辅助方法 ====================

        private void AddField(List<string> columns, List<string> values, string column, string? value)
        {
            if (value != null)
            {
                columns.Add(column);
                sql.Parameters[$"@{column}"] = value;
                values.Add($"@{column}");
            }
        }

        private void AddSetClause(List<string> clauses, string columnName, string? value)
        {
            if (value != null)
            {
                sql.Parameters[$"@{columnName}"] = value;
                clauses.Add($"{columnName} = @{columnName}");
            }
        }

        private void AddWhereClause(List<string> clauses, string columnName, string? value)
        {
            if (value != null)
            {
                sql.Parameters[$"@{columnName}"] = value;
                clauses.Add($"{(clauses.Count == 0 ? "WHERE " : "AND ")}{columnName} = @{columnName}");
            }
        }

        private static string JoinNonEmpty(string separator, params string[] parts)
            => string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));

        private static string Prefix(string label, string value)
            => string.IsNullOrWhiteSpace(value) ? "" : label + value;

        private static void AppendLineIfAny(StringBuilder sb, string line)
        {
            if (!string.IsNullOrWhiteSpace(line)) sb.AppendLine(line);
        }

        private static string EscapeLikeWildcards(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            return input.Replace("%", "\\%").Replace("_", "\\_");
        }

        public class MapMatch
        {
            public int Id { get; set; }
            public string DisplayName { get; set; } = "";
        }
    }
}
