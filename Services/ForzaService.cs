using System.Data;
using System.Text;
using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Utilities;
using Milimoe.FunGame.Core.Api.Transmittal;

namespace CandyCottage.CandyBot.Services
{
    public class ForzaService(ILogger<ForzaService> logger, SQLHelper sql)
    {
        public (bool success, string message, DataTable? data, int totalPages) GetVehicleDataTable(string whereClause, int page, int pageSize = 12)
        {
            if (page < 1) page = 1;
            int offset = (page - 1) * pageSize;

            string query = $@"
                SELECT 
                    Id, Class, MatchType, Manufacturer, Model, Year, Drivetrain,
                    Attribute, TireCompound, SurfaceCondition, Strength, Tune, Remarks, LapTime
                FROM Vehicles
                {whereClause}
                ORDER BY Class, Manufacturer, Model
                LIMIT {offset}, {pageSize}";

            // 参数传不过来，临时不清参数
            sql.ClearParametersAfterExecute = false;
            sql.ExecuteDataSet(query);
            sql.ClearParametersAfterExecute = true;
            if (!sql.Success) return (false, "查询失败，请稍后重试。", null, 0);

            DataTable dt = sql.DataSet.Tables[0];

            // 计算总记录数
            string countQuery = $"SELECT COUNT(*) FROM Vehicles {whereClause}";
            DataRow? dr = sql.ExecuteDataRow(countQuery);
            int totalCount = 0;
            if (sql.Success && dr != null) _ = int.TryParse(dr[0].ToString(), out totalCount);

            int totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
            return (true, "success", dt, totalPages);
        }

        public (bool success, string message, DataTable? data, int totalPages) GetVehicleById(int id)
        {
            sql.Parameters["@id"] = id;
            return GetVehicleDataTable("WHERE Id = @id", 1, 1);
        }

        public (bool success, string message, DataTable? data, int totalPages) GetVehicleInfoRaw(string vehicleName, int page)
        {
            if (string.IsNullOrWhiteSpace(vehicleName))
                return (false, "请提供车辆名称关键字（制造商或型号）。", null, 0);

            string searchPattern = $"%{EscapeLikeWildcards(vehicleName)}%";
            sql.Parameters["@search"] = searchPattern;
            string whereClause = "WHERE Manufacturer LIKE @search OR Model LIKE @search";
            var (success, message, data, totalPages) = GetVehicleDataTable(whereClause, page);
            return (success, message, data, totalPages);
        }

        public (bool success, string message, DataTable? data, int totalPages) GetVehiclesByClassAndMatchTypeRaw(string? classInput, string? matchTypeInput, int page)
        {
            List<string> conditions = [];
            if (!string.IsNullOrEmpty(classInput))
            {
                sql.Parameters["@class"] = classInput;
                conditions.Add("Class = @class");
            }
            if (!string.IsNullOrEmpty(matchTypeInput))
            {
                sql.Parameters["@matchType"] = matchTypeInput;
                conditions.Add("MatchType = @matchType");
            }

            string whereClause = conditions.Count == 0 ? "" : "WHERE " + string.Join(" AND ", conditions);
            var (success, message, data, totalPages) = GetVehicleDataTable(whereClause, page);
            return (success, message, data, totalPages);
        }

        public (bool, string) GetVehicleInfo(string vehicleName, int page, out int totalPages)
        {
            totalPages = page;
            if (string.IsNullOrWhiteSpace(vehicleName))
                return (false, "请提供车辆名称关键字（制造商或型号）。");

            string searchPattern = $"%{EscapeLikeWildcards(vehicleName)}%";
            sql.Parameters["@search"] = searchPattern;
            string whereClause = "WHERE Manufacturer LIKE @search OR Model LIKE @search";

            return ExecuteVehicleQuery(whereClause, page, out totalPages);
        }

        public (bool, string) GetVehiclesByClassOrMatchType(string? classInput, string? matchTypeInput, int page, out int totalPages)
        {
            List<string> conditions = [];
            if (classInput != null)
            {
                sql.Parameters["@class"] = classInput;
                conditions.Add("Class = @class");
            }
            if (matchTypeInput != null)
            {
                sql.Parameters["@matchType"] = matchTypeInput;
                conditions.Add("MatchType = @matchType");
            }

            string whereClause = conditions.Count == 0 ? "" : "WHERE " + string.Join(" OR ", conditions);
            return ExecuteVehicleQuery(whereClause, page, out totalPages);
        }

        public (bool, string) GetVehiclesByClassAndMatchType(string? classInput, string? matchTypeInput, int page, out int totalPages)
        {
            List<string> conditions = [];
            if (classInput != null)
            {
                sql.Parameters["@class"] = classInput;
                conditions.Add("Class = @class");
            }
            if (matchTypeInput != null)
            {
                sql.Parameters["@matchType"] = matchTypeInput;
                conditions.Add("MatchType = @matchType");
            }

            string whereClause = conditions.Count == 0 ? "" : "WHERE " + string.Join(" AND ", conditions);
            return ExecuteVehicleQuery(whereClause, page, out totalPages);
        }

        public bool UpdateVehicle(VehicleRequest request, out string error)
        {
            error = "";
            if (sql == null)
            {
                error = "数据库连接失败。";
                return false;
            }

            try
            {
                sql.NewTransaction();

                // ----- 定位车辆 -----
                int vehicleId = 0;
                if (request.Id.HasValue && request.Id.Value > 0)
                {
                    sql.Parameters["@id"] = request.Id.Value;
                    sql.ExecuteDataSetForUpdate("SELECT Id FROM Vehicles WHERE Id = @id");
                }
                else
                {
                    error = "请提供车辆ID以定位车辆。";
                    return false;
                }

                if (!sql.Success || sql.DataSet.Tables[0].Rows.Count == 0)
                {
                    error = "未找到对应的车辆。请检查定位条件是否正确。";
                    return false;
                }

                vehicleId = Convert.ToInt32(sql.DataSet.Tables[0].Rows[0]["Id"]);

                // ----- 构建 UPDATE 语句 -----
                List<string> setClauses = [];
                AddSetClause(setClauses, "Manufacturer", request.Manufacturer);
                AddSetClause(setClauses, "Model", request.Model);
                AddSetClause(setClauses, "Year", request.Year);
                AddSetClause(setClauses, "Class", request.Class);
                AddSetClause(setClauses, "MatchType", request.MatchType);
                AddSetClause(setClauses, "Drivetrain", request.Drivetrain);
                AddSetClause(setClauses, "Attribute", request.Attribute);
                AddSetClause(setClauses, "TireCompound", request.TireCompound);
                AddSetClause(setClauses, "SurfaceCondition", request.SurfaceCondition);
                AddSetClause(setClauses, "Strength", request.Strength);
                AddSetClause(setClauses, "Tune", request.Tune);
                AddSetClause(setClauses, "Remarks", request.Remarks);
                AddSetClause(setClauses, "LapTime", request.LapTime);
                AddSetClause(setClauses, "ExtData", request.ExtData);

                if (setClauses.Count == 0)
                {
                    error = "没有提供任何修改参数。";
                    return false;
                }

                string updateSql = $"UPDATE Vehicles SET {string.Join(", ", setClauses)} WHERE Id = @id";
                sql.Parameters["@id"] = vehicleId;
                sql.Execute(updateSql);
                if (!sql.Success)
                {
                    error = "更新车辆信息失败。";
                    return false;
                }

                sql.Commit();
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error: {Message}", ex.Message);
                error = $"修改车辆异常：{ex.Message}";
                return false;
            }
            finally
            {
                sql.Rollback();
            }
        }

        public bool AddVehicle(VehicleRequest request, out string error)
        {
            error = "";

            // 校验必填字段
            if (request.Manufacturer is null)
            {
                error = "制造商不能为空。";
                return false;
            }
            if (request.Model is null)
            {
                error = "型号不能为空。";
                return false;
            }
            if (request.Class is null)
            {
                error = "等级不能为空。";
                return false;
            }
            if (request.MatchType is null)
            {
                error = "比赛类型不能为空。";
                return false;
            }
            if (request.Drivetrain is null)
            {
                error = "驱动类型不能为空。";
                return false;
            }

            try
            {
                sql.NewTransaction();

                // 构建 INSERT 语句
                List<string> columns = [];
                List<string> values = [];

                AddField(columns, values, "Manufacturer", request.Manufacturer);
                AddField(columns, values, "Model", request.Model);
                AddField(columns, values, "Year", request.Year);
                AddField(columns, values, "Class", request.Class);
                AddField(columns, values, "MatchType", request.MatchType);
                AddField(columns, values, "Drivetrain", request.Drivetrain);
                AddField(columns, values, "Attribute", request.Attribute);
                AddField(columns, values, "TireCompound", request.TireCompound);
                AddField(columns, values, "SurfaceCondition", request.SurfaceCondition);
                AddField(columns, values, "Strength", request.Strength);
                AddField(columns, values, "Tune", request.Tune);
                AddField(columns, values, "Remarks", request.Remarks);
                AddField(columns, values, "LapTime", request.LapTime);
                AddField(columns, values, "ExtData", request.ExtData);

                string insertSql = $"INSERT INTO Vehicles ({string.Join(", ", columns)}) VALUES ({string.Join(", ", values)})";
                sql.Execute(insertSql);

                if (!sql.Success)
                {
                    error = "新增车辆失败，请稍后重试。";
                    return false;
                }

                sql.Commit();
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AddVehicle error: {Message}", ex.Message);
                error = $"新增车辆异常：{ex.Message}";
                return false;
            }
            finally
            {
                sql.Rollback();
            }
        }

        public (bool success, string message, int deletedCount) DeleteVehiclesByIds(List<int> ids)
        {
            if (ids == null || ids.Count == 0)
                return (false, "未提供有效的车辆ID", 0);

            try
            {
                sql.NewTransaction();
                string idList = string.Join(",", ids);
                string inClause = string.Join(",", ids.Select((_, i) => $"@id{i}"));
                for (int i = 0; i < ids.Count; i++)
                {
                    sql.Parameters[$"@id{i}"] = ids[i];
                }
                string query = $"DELETE FROM Vehicles WHERE Id IN ({inClause})";
                sql.Execute(query);
                if (!sql.Success)
                    return (false, "删除失败，请稍后重试", 0);

                sql.Commit();
                return (true, $"成功删除 {ids.Count} 辆车", ids.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "DeleteVehiclesByIds error");
                return (false, $"删除异常：{ex.Message}", 0);
            }
            finally
            {
                sql.Rollback();
            }
        }

        public (bool success, string message, List<VehicleMatch>? matches) FindVehiclesByCriteria(VehicleRequest request)
        {
            List<VehicleMatch> matches = [];

            try
            {
                sql.Parameters.Clear();
                List<string> whereClauses = [];
                AddWhereClause(whereClauses, "Manufacturer", request.Manufacturer);
                AddWhereClause(whereClauses, "Model", request.Model);
                AddWhereClause(whereClauses, "Year", request.Year);
                AddWhereClause(whereClauses, "Class", request.Class);
                AddWhereClause(whereClauses, "MatchType", request.MatchType);
                AddWhereClause(whereClauses, "Drivetrain", request.Drivetrain);
                AddWhereClause(whereClauses, "Attribute", request.Attribute);
                AddWhereClause(whereClauses, "TireCompound", request.TireCompound);
                AddWhereClause(whereClauses, "SurfaceCondition", request.SurfaceCondition);
                AddWhereClause(whereClauses, "Strength", request.Strength);
                AddWhereClause(whereClauses, "Tune", request.Tune);
                AddWhereClause(whereClauses, "Remarks", request.Remarks);
                AddWhereClause(whereClauses, "LapTime", request.LapTime);

                string query = @$"
                    SELECT Id, Manufacturer, Model, Year, Class, MatchType, Drivetrain, Strength
                    FROM Vehicles
                    {string.Join(" ", whereClauses)}";

                sql.ExecuteDataSet(query);
                if (!sql.Success || sql.DataSet.Tables[0].Rows.Count == 0)
                    return (false, "未找到匹配的车辆", null);

                DataTable dt = sql.DataSet.Tables[0];
                foreach (DataRow row in dt.Rows)
                {
                    string display = $"{row["Manufacturer"]} {row["Model"]} ({row["Year"]}) [{row["Class"]}/{row["MatchType"]}]";
                    matches.Add(new VehicleMatch
                    {
                        Id = Convert.ToInt32(row["Id"]),
                        DisplayName = display
                    });
                }

                return (true, $"找到 {matches.Count} 辆匹配的车辆", matches);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "FindVehiclesByCriteria error");
                return (false, $"查找异常：{ex.Message}", null);
            }
        }

        private (bool, string) ExecuteVehicleQuery(string whereClause, int page, out int totalPages)
        {
            totalPages = page;
            if (page < 1) page = 1;
            const int pageSize = 12;
            int offset = (page - 1) * pageSize;

            string query = $@"
                SELECT
                    Id, Class, MatchType, Manufacturer, Model, Year, Drivetrain,
                    Attribute, TireCompound, SurfaceCondition, Strength, Tune, Remarks, LapTime
                FROM Vehicles
                {whereClause}
                ORDER BY Class, Manufacturer, Model
                LIMIT {offset}, {pageSize}";

            sql.ClearParametersAfterExecute = false;
            sql.ExecuteDataSet(query);
            sql.ClearParametersAfterExecute = true;
            if (!sql.Success)
                return (false, "查询失败，请稍后重试。");

            DataTable dt = sql.DataSet.Tables[0];
            if (dt.Rows.Count == 0)
                return (false, whereClause.Contains("LIKE") ? "未找到相关车辆。" : "未找到符合条件的车辆。");

            // 渲染为卡片/分行布局：每辆车一张卡片，只输出非空字段，移动端比 13 列宽表更易读
            string body = RenderVehiclesAsCards(dt);

            // 计算总页数
            string countQuery = $"SELECT COUNT(*) FROM Vehicles {whereClause}";
            DataRow? dr = sql.ExecuteDataRow(countQuery);
            int totalCount = 0;
            if (sql.Success && dr != null)
                _ = int.TryParse(dr[0].ToString(), out totalCount);

            if (totalCount > page * pageSize)
                totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);

            return (true, body);
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
                clauses.Add(($"{(clauses.Count == 0 ? "WHERE " : "AND ")}{columnName} = @{columnName}"));
            }
        }

        /// <summary>
        /// 将车辆查询结果渲染为「卡片/分行」式 Markdown 文本，每辆车一张卡片，卡片间用分隔线隔开。
        /// 只输出非空字段，避免空列占据移动端消息卡片的宽度。这是纯呈现逻辑，不触碰数据库——
        /// 调用方负责传入已查好的 DataTable（列须包含 Id 及各车辆字段）。
        /// </summary>
        private static string RenderVehiclesAsCards(DataTable dt)
        {
            StringBuilder sb = new();
            for (int i = 0; i < dt.Rows.Count; i++)
            {
                DataRow row = dt.Rows[i];
                string Cell(string col) => dt.Columns.Contains(col) ? (row[col]?.ToString() ?? "").Trim() : "";

                // 标题行：制造商 型号 (年份) · 等级/类型 · ID —— ID 让管理员能直接「修改车辆 <ID>」
                string title = $"🏎 {Cell("Manufacturer")} {Cell("Model")}".TrimEnd();
                title += Prefix(" ", Cell("Year") == "" ? "" : $"({Cell("Year")})");
                title += $" · {Cell("Class")}/{Cell("MatchType")}";
                title += Prefix(" · ID ", Cell("Id"));
                sb.AppendLine(title);

                // 传动 | 属性 | 强度
                AppendLineIfAny(sb, JoinNonEmpty(" | ", Cell("Drivetrain"), Cell("Attribute"), Prefix("强度 ", Cell("Strength"))));
                // 胎面 · 干/湿
                AppendLineIfAny(sb, JoinNonEmpty(" · ", Prefix("胎面 ", Cell("TireCompound")), Cell("SurfaceCondition")));
                AppendLineIfAny(sb, Prefix("调校 ", Cell("Tune")));
                AppendLineIfAny(sb, Prefix("备注 ", Cell("Remarks")));
                AppendLineIfAny(sb, Prefix("圈速 ", Cell("LapTime")));

                if (i < dt.Rows.Count - 1) sb.AppendLine("━━━━━━━━━━━━━━━");
            }
            return sb.ToString();
        }

        /// <summary>用分隔符连接若干字段，自动跳过空白片段（避免出现 "四驱 |  | 强度" 这类空洞）。</summary>
        private static string JoinNonEmpty(string separator, params string[] parts)
            => string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));

        /// <summary>给非空值加前缀标签；值为空白时返回空串（连标签一起省略）。</summary>
        private static string Prefix(string label, string value)
            => string.IsNullOrWhiteSpace(value) ? "" : label + value;

        /// <summary>仅当文本非空白时才追加一行，避免输出空行。</summary>
        private static void AppendLineIfAny(StringBuilder sb, string line)
        {
            if (!string.IsNullOrWhiteSpace(line)) sb.AppendLine(line);
        }

        private static string EscapeLikeWildcards(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            // 转义 SQL LIKE 通配符（% 和 _），防止用户输入干扰 LIKE 语义。
            return input.Replace("%", "\\%").Replace("_", "\\_");
        }

        private void AddField(List<string> columns, List<string> values, string column, string? value)
        {
            if (value != null)
            {
                columns.Add(column);
                sql.Parameters[$"@{column}"] = value;
                values.Add($"@{column}");
            }
        }

        public class VehicleMatch
        {
            public int Id { get; set; }
            public string DisplayName { get; set; } = "";
        }
    }
}
