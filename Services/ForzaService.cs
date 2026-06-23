using System.Data;
using System.Text;
using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Utilities;
using Milimoe.FunGame.Core.Api.Transmittal;

namespace CandyCottage.CandyBot.Services
{
    public class ForzaService(ILogger<ForzaService> logger, SQLHelper sql, ForzaConfig forzaConfig)
    {
        public (bool success, string message, DataTable? data, int totalPages) GetVehicleDataTable(string whereClause, int page, int pageSize = 12)
        {
            if (page < 1) page = 1;
            int offset = (page - 1) * pageSize;

            // 每调校一卡：从 Vehicles v JOIN VehicleTunes t 读取，SELECT 用旧别名保持渲染器兼容性。
            // whereClause 中的列引用须已由调用方 table-qualify（v.Manufacturer/v.Model、t.Class/t.MatchType 等）。
            string fromJoin = "FROM Vehicles v JOIN VehicleTunes t ON t.VehicleId = v.Id";
            string query = $@"
                SELECT
                    t.Id AS Id, t.Class, t.MatchType,
                    v.Manufacturer, v.Model, v.Year,
                    t.Drivetrain,
                    t.PerformancePreference AS Attribute,
                    t.TireCompound, t.SurfaceCondition, t.Strength,
                    t.TuneRaw AS Tune,
                    t.Remarks, t.LapTime
                {fromJoin}
                {whereClause}
                ORDER BY t.Class, v.Manufacturer, v.Model
                LIMIT {offset}, {pageSize}";

            // 参数需在 SELECT 和 COUNT 之间复用，临时不清参数
            sql.ClearParametersAfterExecute = false;
            sql.ExecuteDataSet(query);
            sql.ClearParametersAfterExecute = true;

            if (sql.DataSet is null || sql.DataSet.Tables.Count == 0 || sql.DataSet.Tables[0].Rows.Count == 0)
                return (false, whereClause.Contains("LIKE") ? "未找到相关车辆。" : "未找到符合条件的车辆。", null, 0);

            DataTable dt = sql.DataSet.Tables[0];

            // COUNT 与 SELECT 同 FROM/JOIN/WHERE，计数调校行（每调校一卡）
            string countQuery = $"SELECT COUNT(*) {fromJoin} {whereClause}";
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

        // ==================== 车型 CRUD（Task 7：ModelKey 唯一，确定性返回 VehicleId） ====================

        /// <summary>
        /// 按 ModelKey upsert 车型，并通过回读确定性地返回 VehicleId（只增不删）。
        ///
        /// 不依赖 LastInsertId：INSERT...ON DUPLICATE KEY 在 MySQL 的实际 LAST_INSERT_ID 行为因
        /// 版本和参数而异；回读 SELECT 总是正确。新行的 IsManual 由 actorUserId 决定：
        /// actorUserId != 0 表示手动创建（IsManual=1），0 表示系统/自动同步（IsManual=0）。
        /// 已存在的行不会被覆写 IsManual。
        ///
        /// 前置：Manufacturer/Model 不能为空；Year 为 null 或 "" 归一化后相同（不产生新行）。
        /// </summary>
        /// <param name="request">车型信息（Manufacturer、Model 必填，Year 可选）。</param>
        /// <param name="actorUserId">操作人用户ID；0 表示系统/自动同步。</param>
        /// <param name="error">出错时的描述；成功时为 ""。</param>
        /// <returns>upsert 后 Vehicles.Id；出错时返回 0。</returns>
        public int EnsureVehicleModel(VehicleModelRequest request, long actorUserId, out string error)
        {
            error = "";

            // 校验必填字段
            if (string.IsNullOrWhiteSpace(request.Manufacturer))
            {
                error = "制造商不能为空。";
                return 0;
            }
            if (string.IsNullOrWhiteSpace(request.Model))
            {
                error = "型号不能为空。";
                return 0;
            }

            string modelKey = ForzaTuneIdentity.BuildModelKey(request.Manufacturer, request.Model, request.Year);
            int isManual = actorUserId != 0 ? 1 : 0;

            try
            {
                sql.NewTransaction();

                // ON DUPLICATE KEY 只更新 Id=LAST_INSERT_ID(Id) 使得行保持不变，并激活 LAST_INSERT_ID；
                // 但回读 SELECT 更可靠，避免 MySQL 驱动/版本之间 LastInsertId 的歧义。
                sql.Parameters["@manufacturer"] = request.Manufacturer;
                sql.Parameters["@model"] = request.Model;
                sql.Parameters["@year"] = request.Year ?? (object)DBNull.Value;
                sql.Parameters["@modelKey"] = modelKey;
                sql.Parameters["@isManual"] = isManual;
                sql.Parameters["@userId"] = actorUserId;
                sql.Execute(@"
                    INSERT INTO Vehicles (Manufacturer, Model, Year, ModelKey, IsManual, LastUpdatedUserId)
                    VALUES (@manufacturer, @model, @year, @modelKey, @isManual, @userId)
                    ON DUPLICATE KEY UPDATE Id = LAST_INSERT_ID(Id)");

                if (!sql.Success)
                {
                    error = "写入车型失败，请稍后重试。";
                    return 0;
                }

                sql.Commit();

                // 回读以确定性获取 VehicleId（不信任 LastInsertId）
                sql.Parameters["@key"] = modelKey;
                DataRow? row = sql.ExecuteDataRow("SELECT Id FROM Vehicles WHERE ModelKey = @key");
                if (!sql.Success || row == null)
                {
                    error = "写入后回读车型失败。";
                    return 0;
                }

                // 新制造商加入后使制造商缓存失效
                forzaConfig.Invalidate();
                return Convert.ToInt32(row["Id"]);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "EnsureVehicleModel error: {Message}", ex.Message);
                error = $"车型 upsert 异常：{ex.Message}";
                return 0;
            }
            finally
            {
                sql.Rollback();
            }
        }

        /// <summary>
        /// 按主键查询单条车型记录，返回统一的 (success, message, DataTable?, totalPages) 形态。
        /// totalPages 固定为 1（车型按主键精确匹配，不分页）。
        /// </summary>
        public (bool success, string message, DataTable? data, int totalPages) GetVehicleModelById(int id)
        {
            sql.Parameters["@id"] = id;
            sql.ExecuteDataSet("SELECT * FROM Vehicles WHERE Id = @id");
            if (!sql.Success || sql.DataSet is null || sql.DataSet.Tables.Count == 0
                || sql.DataSet.Tables[0].Rows.Count == 0)
                return (false, "未找到对应的车型。", null, 0);
            return (true, "success", sql.DataSet.Tables[0], 1);
        }

        /// <summary>
        /// 更新车型的 Manufacturer/Model/Year 并重新计算 ModelKey。
        /// request.Id 为必填；Manufacturer 和 Model 不能更新为空。
        /// LastUpdatedUserId 更新为 actorUserId。
        /// </summary>
        public bool UpdateVehicleModel(VehicleModelRequest request, long actorUserId, out string error)
        {
            error = "";

            if (!request.Id.HasValue || request.Id.Value <= 0)
            {
                error = "请提供车型ID。";
                return false;
            }
            if (string.IsNullOrWhiteSpace(request.Manufacturer))
            {
                error = "制造商不能为空。";
                return false;
            }
            if (string.IsNullOrWhiteSpace(request.Model))
            {
                error = "型号不能为空。";
                return false;
            }

            string modelKey = ForzaTuneIdentity.BuildModelKey(request.Manufacturer, request.Model, request.Year);

            try
            {
                sql.NewTransaction();

                // 确认记录存在（加行锁）
                sql.Parameters["@id"] = request.Id.Value;
                sql.ExecuteDataSetForUpdate("SELECT Id FROM Vehicles WHERE Id = @id");
                if (!sql.Success || sql.DataSet.Tables[0].Rows.Count == 0)
                {
                    error = "未找到对应的车型。";
                    return false;
                }

                sql.Parameters["@manufacturer"] = request.Manufacturer;
                sql.Parameters["@model"] = request.Model;
                sql.Parameters["@year"] = request.Year ?? (object)DBNull.Value;
                sql.Parameters["@modelKey"] = modelKey;
                sql.Parameters["@userId"] = actorUserId;
                sql.Parameters["@id"] = request.Id.Value;
                sql.Execute(@"
                    UPDATE Vehicles
                    SET Manufacturer = @manufacturer,
                        Model = @model,
                        Year = @year,
                        ModelKey = @modelKey,
                        LastUpdatedUserId = @userId
                    WHERE Id = @id");

                if (!sql.Success)
                {
                    error = "更新车型失败。";
                    return false;
                }

                sql.Commit();
                // 制造商可能被改名，失效缓存
                forzaConfig.Invalidate();
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "UpdateVehicleModel error: {Message}", ex.Message);
                error = $"更新车型异常：{ex.Message}";
                return false;
            }
            finally
            {
                sql.Rollback();
            }
        }

        public (bool success, string message, DataTable? data, int totalPages) GetVehicleInfoRaw(string vehicleName, int page)
        {
            if (string.IsNullOrWhiteSpace(vehicleName))
                return (false, "请提供车辆名称关键字（制造商或型号）。", null, 0);

            string whereClause = BuildNameSearchWhereClause(vehicleName);
            var (success, message, data, totalPages) = GetVehicleDataTable(whereClause, page);
            return (success, message, data, totalPages);
        }

        /// <summary>
        /// 构建“单关键字车辆名称搜索”的 WHERE 子句并绑定参数，按以下条件 OR 匹配：
        /// <list type="number">
        /// <item>制造商 / 型号单列 LIKE；</item>
        /// <item>“制造商+型号”去空格后的紧凑组合——数据中制造商形如 "Ferrari 法拉利"，
        /// 用户输入“法拉利FXX”“法拉利 FXX”不含完整制造商串，单列 LIKE 无法命中，
        /// 紧凑组合 "Ferrari法拉利FXX" 可以。CONCAT 在 MySQL 与捆绑的 SQLite（3.49）上语法一致；
        /// 不要改用 ||（MySQL 默认将其解析为逻辑 OR）。Manufacturer/Model 均为 NOT NULL，无需空值防护；</item>
        /// <item>制造商别名组合——“FerrariFXX”这类输入“法拉利”隔在中间，紧凑组合也无法命中，
        /// 由 <see cref="ResolveManufacturerAliasSearches"/> 拆成“真实制造商 + 型号关键字”精确分支。</item>
        /// </list>
        /// </summary>
        private string BuildNameSearchWhereClause(string keyword)
        {
            // 别名解析内部会执行 SQL，必须先于参数绑定（执行后 ClearParametersAfterExecute 会清空已绑定参数）
            List<(string Manufacturer, string ModelKeyword)> aliasSearches = ResolveManufacturerAliasSearches(keyword);

            sql.Parameters["@search"] = $"%{EscapeLikeWildcards(keyword)}%";
            sql.Parameters["@compactSearch"] = $"%{EscapeLikeWildcards(keyword.Replace(" ", ""))}%";
            // JOIN 路径后必须 table-qualify 所有来自 Vehicles 的列
            List<string> conditions =
            [
                "v.Manufacturer LIKE @search",
                "v.Model LIKE @search",
                "REPLACE(CONCAT(v.Manufacturer, v.Model), ' ', '') LIKE @compactSearch",
            ];

            for (int i = 0; i < aliasSearches.Count; i++)
            {
                sql.Parameters[$"@aliasManufacturer{i}"] = aliasSearches[i].Manufacturer;
                sql.Parameters[$"@aliasModel{i}"] = $"%{EscapeLikeWildcards(aliasSearches[i].ModelKeyword)}%";
                conditions.Add($"(v.Manufacturer = @aliasManufacturer{i} AND REPLACE(v.Model, ' ', '') LIKE @aliasModel{i})");
            }

            return "WHERE " + string.Join(" OR ", conditions);
        }

        /// <summary>
        /// 解析“制造商别名 + 型号”组合输入：从数据库实时读取去重制造商（如果 ForzaConfig 内存缓存为空）
        /// 把每个制造商按空格拆 token，生成自左 token 前缀与自右 token 后缀
        /// 作为别名（如 "Aston Martin 阿斯顿·马丁" → Aston、AstonMartin、阿斯顿·马丁……）；
        /// 去空格后的输入以某别名开头且剩余非空时，得到一个 (真实制造商, 型号关键字) 候选。
        /// 多个别名命中时只保留最长别名的候选，防止 Aston 抢走 AstonMartin。
        /// 任何一步失败（如查询失败、无候选）都返回空列表，调用方退化为基础 LIKE 搜索。
        /// </summary>
        private List<(string Manufacturer, string ModelKeyword)> ResolveManufacturerAliasSearches(string keyword)
        {
            string compactKeyword = keyword.Replace(" ", "");
            if (compactKeyword == "") return [];

            List<(int AliasLength, string Manufacturer, string ModelKeyword)> candidates = [];
            IReadOnlyList<string> cached = forzaConfig.Manufacturers;
            if (cached.Count == 0)
            {
                sql.ExecuteDataSet("SELECT DISTINCT Manufacturer FROM Vehicles");
                if (!sql.Success || sql.DataSet.Tables.Count == 0) return [];

                List<string> loaded = [];
                foreach (DataRow row in sql.DataSet.Tables[0].Rows)
                {
                    string manufacturer = row["Manufacturer"].ToString() ?? "";
                    loaded.Add(manufacturer);
                    SplitManufacturers(compactKeyword, manufacturer, candidates);
                }
                // 回填缓存：否则重启后（或缓存失效后）每次组合搜索都会重复这条 DISTINCT 查询
                forzaConfig.Update(loaded);
            }
            else
            {
                foreach (string manufacturer in cached)
                {
                    SplitManufacturers(compactKeyword, manufacturer, candidates);
                }
            }

            if (candidates.Count == 0) return [];
            int maxLength = candidates.Max(c => c.AliasLength);
            return [.. candidates.Where(c => c.AliasLength == maxLength).Select(c => (c.Manufacturer, c.ModelKeyword)).Distinct()];
        }

        private static void SplitManufacturers(string compactKeyword, string manufacturer, List<(int AliasLength, string Manufacturer, string ModelKeyword)> candidates)
        {
            string[] tokens = manufacturer.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) return;

            HashSet<string> aliases = new(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i <= tokens.Length; i++)
            {
                aliases.Add(string.Concat(tokens[..i]));  // 自左前缀：Aston、AstonMartin、……
                aliases.Add(string.Concat(tokens[^i..])); // 自右后缀：阿斯顿·马丁、Martin阿斯顿·马丁、……
            }

            foreach (string alias in aliases)
            {
                // 输入必须严格长于别名：剩余部分作为型号关键字，纯厂商搜索不进组合分支
                if (compactKeyword.Length > alias.Length &&
                    compactKeyword.StartsWith(alias, StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add((alias.Length, manufacturer, compactKeyword[alias.Length..]));
                }
            }
        }

        public (bool success, string message, DataTable? data, int totalPages) GetVehiclesByClassAndMatchTypeRaw(string? classInput, string? matchTypeInput, int page)
        {
            List<string> conditions = [];
            if (!string.IsNullOrEmpty(classInput))
            {
                sql.Parameters["@class"] = classInput;
                // JOIN 路径用 t.Class（来自 VehicleTunes）
                conditions.Add("t.Class = @class");
            }
            if (!string.IsNullOrEmpty(matchTypeInput))
            {
                sql.Parameters["@matchType"] = matchTypeInput;
                conditions.Add("t.MatchType = @matchType");
            }

            string whereClause = conditions.Count == 0 ? "" : "WHERE " + string.Join(" AND ", conditions);
            var (success, message, data, totalPages) = GetVehicleDataTable(whereClause, page);
            return (success, message, data, totalPages);
        }

        public (bool, string) GetVehicleInfo(string? manufacturer, string? model, bool useOr, int page, out int totalPages)
        {
            // OR 回退仅用于“解析不出制造商”的单关键字场景（当前唯一调用方传入 manufacturer == model == 原始输入），
            // 与 GetVehicleInfoRaw 共用同一名称搜索条件，使“法拉利FXX”这类组合名也能命中。
            if (useOr)
            {
                return ExecuteVehicleQuery(BuildNameSearchWhereClause(manufacturer ?? model ?? ""), page, out totalPages);
            }

            List<string> conditions = [];
            if (!string.IsNullOrEmpty(manufacturer))
            {
                sql.Parameters["@manufacturer"] = $"%{EscapeLikeWildcards(manufacturer)}%";
                // JOIN 路径用 v.Manufacturer（来自 Vehicles）
                conditions.Add("v.Manufacturer LIKE @manufacturer");
            }
            if (!string.IsNullOrEmpty(model))
            {
                sql.Parameters["@model"] = $"%{EscapeLikeWildcards(model)}%";
                conditions.Add("v.Model LIKE @model");
            }

            string whereClause = conditions.Count == 0 ? "" : "WHERE " + string.Join(" AND ", conditions);
            return ExecuteVehicleQuery(whereClause, page, out totalPages);
        }

        public (bool, string) GetVehiclesByClassOrMatchType(string? classInput, string? matchTypeInput, int page, out int totalPages)
        {
            List<string> conditions = [];
            if (classInput != null)
            {
                sql.Parameters["@class"] = classInput;
                // JOIN 路径用 t.Class（来自 VehicleTunes）
                conditions.Add("t.Class = @class");
            }
            if (matchTypeInput != null)
            {
                sql.Parameters["@matchType"] = matchTypeInput;
                conditions.Add("t.MatchType = @matchType");
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
                // JOIN 路径用 t.Class（来自 VehicleTunes）
                conditions.Add("t.Class = @class");
            }
            if (matchTypeInput != null)
            {
                sql.Parameters["@matchType"] = matchTypeInput;
                conditions.Add("t.MatchType = @matchType");
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
                // 制造商可能被改名，失效缓存
                forzaConfig.Invalidate();
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

        /// <summary>
        /// 手动新增一条调校（人手维护）。
        /// req.VehicleId 必填且必须已存在；Class/MatchType/Drivetrain 必填。
        /// 手动调校无来源：SourceFileId=""，SourceIdentity="" — InsertTune 按此处理；
        /// 用全局 seq 在 ('','') 槽内消歧，避免 uq_source_identity 冲突。
        /// </summary>
        /// <returns>新行的 TuneId；失败返回 0 并通过 error 输出错误描述。</returns>
        public int AddTune(VehicleTuneRequest request, long actorUserId, out string error)
        {
            error = "";

            // 校验必填字段
            if (request.VehicleId == null || request.VehicleId == 0)
            {
                error = "车辆ID不能为空。";
                return 0;
            }
            if (string.IsNullOrWhiteSpace(request.Class))
            {
                error = "等级不能为空。";
                return 0;
            }
            if (string.IsNullOrWhiteSpace(request.MatchType))
            {
                error = "比赛类型不能为空。";
                return 0;
            }
            if (string.IsNullOrWhiteSpace(request.Drivetrain))
            {
                error = "驱动类型不能为空。";
                return 0;
            }

            try
            {
                // 校验 VehicleId 存在
                sql.Parameters["@vehicleIdCheck"] = request.VehicleId.Value;
                DataRow? vehicleRow = sql.ExecuteDataRow("SELECT 1 FROM Vehicles WHERE Id = @vehicleIdCheck");
                if (!sql.Success || vehicleRow == null)
                {
                    error = $"车辆ID {request.VehicleId.Value} 不存在，请先使用\"新增车型\"创建车型。";
                    return 0;
                }

                // 手动路径：SourceFileId=""，IsManual=true
                request.SourceFileId = "";
                request.IsManual = true;
                request.LastUpdatedUserId = actorUserId;

                // 计算手动调校的 seq（在 SourceFileId='', SourceIdentity='' 槽内全局递增）
                DataRow? seqRow = sql.ExecuteDataRow(
                    "SELECT COALESCE(MAX(SourceIdentitySeq), -1) FROM VehicleTunes WHERE SourceFileId = '' AND SourceIdentity = ''");
                int seq = 0;
                if (sql.Success && seqRow != null)
                {
                    seq = Convert.ToInt32(seqRow[0]) + 1;
                }

                sql.NewTransaction();
                int tuneId = InsertTune(request, seq, out error);
                if (tuneId == 0)
                    return 0;
                sql.Commit();
                return tuneId;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AddTune error: {Message}", ex.Message);
                error = $"新增调校异常：{ex.Message}";
                return 0;
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
                if (!InsertVehicle(request, out error))
                {
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

        /// <summary>
        /// 构建并执行单条车辆 INSERT，<b>不开启自身事务</b>——由调用方负责事务边界
        /// （commit / rollback）。这使得导入器可以在一个外层事务内 DELETE + 循环插入 N 辆车，
        /// 任一失败即整体回滚。<c>AddVehicle</c> 在校验必填字段后包一层事务委托至此。
        /// </summary>
        /// <remarks>internal 仅为便于集成测试直接验证「外层事务可回滚本插入」（见 InternalsVisibleTo）。</remarks>
        internal bool InsertVehicle(VehicleRequest request, out string error)
        {
            error = "";
            try
            {
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
                // 可能引入新制造商，失效缓存（清空总是安全的，最坏只是下次搜索多一次 DISTINCT 查询）
                forzaConfig.Invalidate();
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "InsertVehicle error: {Message}", ex.Message);
                error = $"新增车辆异常：{ex.Message}";
                return false;
            }
        }

        public (bool success, string message, int deletedCount) DeleteVehiclesByIds(List<int> ids)
        {
            if (ids == null || ids.Count == 0)
                return (false, "未提供有效的车辆ID", 0);

            try
            {
                sql.NewTransaction();
                string inClause = string.Join(",", ids.Select((_, i) => $"@id{i}"));
                for (int i = 0; i < ids.Count; i++)
                {
                    sql.Parameters[$"@id{i}"] = ids[i];
                }
                string query = $"DELETE FROM Vehicles WHERE Id IN ({inClause})";
                sql.Execute(query);
                if (!sql.Success)
                    return (false, "删除失败，请稍后重试", 0);

                int deleted = sql.AffectedRows;
                sql.Commit();
                // 某制造商的最后一辆车可能被删除，失效缓存
                forzaConfig.Invalidate();
                return (true, $"成功删除 {deleted} 辆车", deleted);
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

        /// <summary>
        /// 按车型主键批量删除 Vehicles 行。FK（fk_tune_vehicle ON DELETE CASCADE）负责级联删除对应
        /// 的 VehicleTunes 和 VehicleTuneCache 行，无需在应用层手工删除。
        /// 返回 (success, message, deletedCount)；deletedCount 为 Vehicles 的实际影响行数。
        /// </summary>
        public (bool success, string message, int deletedCount) DeleteVehicleModelsByIds(List<int> ids)
        {
            if (ids == null || ids.Count == 0)
                return (false, "未提供有效的车型ID", 0);

            try
            {
                sql.NewTransaction();
                string inClause = string.Join(",", ids.Select((_, i) => $"@id{i}"));
                for (int i = 0; i < ids.Count; i++)
                    sql.Parameters[$"@id{i}"] = ids[i];

                // FK ON DELETE CASCADE 自动级联删除 VehicleTunes / VehicleTuneCache
                string query = $"DELETE FROM Vehicles WHERE Id IN ({inClause})";
                sql.Execute(query);
                if (!sql.Success)
                    return (false, "删除车型失败，请稍后重试", 0);

                int deleted = sql.AffectedRows;
                sql.Commit();
                // 制造商可能因此完全消失，失效缓存
                forzaConfig.Invalidate();
                return (true, $"成功删除 {deleted} 个车型（含全部关联调校）", deleted);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "DeleteVehicleModelsByIds error");
                return (false, $"删除车型异常：{ex.Message}", 0);
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

        // 面向聊天机器人的查询：复用 GetVehicleDataTable 取数 + 分页，再渲染为卡片文本。
        // 取数/计数/分页逻辑只此一处（GetVehicleDataTable），此方法只负责"空结果文案 + 呈现"。
        private (bool, string) ExecuteVehicleQuery(string whereClause, int page, out int totalPages)
        {
            var (success, msg, data, pages) = GetVehicleDataTable(whereClause, page);
            totalPages = pages;
            if (!success || data == null)
                return (false, msg);

            if (data.Rows.Count == 0)
                return (false, whereClause.Contains("LIKE") ? "未找到相关车辆。" : "未找到符合条件的车辆。");

            // 渲染为卡片/分行布局：每辆车一张卡片，只输出非空字段，移动端比 13 列宽表更易读
            return (true, RenderVehiclesAsCards(data));
        }

        private void AddSetClause(List<string> clauses, string columnName, string? value)
        {
            if (value != null)
            {
                sql.Parameters[$"@{columnName}"] = value;
                clauses.Add($"{columnName} = @{columnName}");
            }
        }

        /// <summary>校验数值/布尔字段：非空时必须为合法数字。</summary>
        private static bool ValidateNumericFields(MapRequest request, out string error)
        {
            error = "";
            if (request.Difficulty != null && !int.TryParse(request.Difficulty, out _))
            { error = "难度必须为整数。"; return false; }
            if (request.TurnCount != null && !int.TryParse(request.TurnCount, out _))
            { error = "弯道数必须为整数。"; return false; }
            if (request.AvgTimeSec != null && !int.TryParse(request.AvgTimeSec, out _))
            { error = "圈速必须为整数（秒）。"; return false; }
            if (request.NightMode != null && (!int.TryParse(request.NightMode, out int nm) || nm < 0 || nm > 1))
            { error = "夜间必须为 0 或 1。"; return false; }
            if (request.IsReverse != null && (!int.TryParse(request.IsReverse, out int rv) || rv < 0 || rv > 1))
            { error = "反向必须为 0 或 1。"; return false; }
            return true;
        }

        /// <summary>将用户输入的别名字符串编码为 JSON 数组。已含方括号则原样保留。</summary>
        private static string? EncodeAliases(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string trimmed = raw.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) return trimmed;
            string[] parts = trimmed.Split([',', '，', '、'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return "[\"" + string.Join("\",\"", parts.Select(p => p.Replace("\"", "\\\""))) + "\"]";
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
        /// <remarks>internal 仅为便于 CandyBot.Tests 直接对纯渲染逻辑做单元测试（见 InternalsVisibleTo）。</remarks>
        internal static string RenderVehiclesAsCards(DataTable dt)
        {
            StringBuilder sb = new();
            for (int i = 0; i < dt.Rows.Count; i++)
            {
                DataRow row = dt.Rows[i];
                string Cell(string col) => dt.Columns.Contains(col) ? (row[col]?.ToString() ?? "").Trim() : "";

                // 标题行：制造商 型号 (年份) · 等级/类型
                string title = $"**{Cell("Manufacturer")} {Cell("Model")}".TrimEnd();
                title += Prefix(" ", Cell("Year") == "" ? "" : $"({Cell("Year")})");
                title += $" · {Cell("Class")}/{Cell("MatchType")}**\r\n> ";
                sb.Append(title);

                // 传动 | 属性 | 强度
                AppendLineIfAny(sb, JoinNonEmpty(" | ", Cell("Drivetrain"), Cell("Attribute"), Prefix("强度：", Cell("Strength"))));
                // 胎面 · 干/湿
                AppendLineIfAny(sb, JoinNonEmpty(" · ", Prefix("胎面：", Cell("TireCompound")), Cell("SurfaceCondition")));
                AppendLineIfAny(sb, Prefix("调校：", Cell("Tune")));
                AppendLineIfAny(sb, Prefix("备注：", Cell("Remarks")));
                AppendLineIfAny(sb, Prefix("圈速：", Cell("LapTime")));

                if (i < dt.Rows.Count - 1) sb.AppendLine("\r\n");
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


        // ==================== 调校 CRUD（Task 8：冻结 identity + 指纹 + 清缓存） ====================

        /// <summary>
        /// 插入一条调校记录，<b>不开启自身事务</b>——由调用方负责事务边界。
        /// seq 为该来源同 identity 的 SourceIdentitySeq（调用方按需传入）。
        ///
        /// 同步路径（SourceFileId 非空）：写入 SourceIdentity / SourceFingerprint。
        /// 手动路径（SourceFileId 为空）：SourceIdentity = ""，SourceFingerprint = NULL。
        ///
        /// 前置：request.VehicleId 必须由调用方先确定；Class/MatchType 为 NOT NULL，缺失即报错。
        /// </summary>
        /// <returns>新行的 TuneId；失败返回 0 并通过 error 输出错误描述。</returns>
        internal int InsertTune(VehicleTuneRequest request, int seq, out string error)
        {
            error = "";

            if (string.IsNullOrWhiteSpace(request.Class))
            {
                error = "等级（Class）不能为空。";
                return 0;
            }
            if (string.IsNullOrWhiteSpace(request.MatchType))
            {
                error = "比赛类型（MatchType）不能为空。";
                return 0;
            }

            try
            {
                string modelKey = ForzaTuneIdentity.BuildModelKey(request.Manufacturer, request.Model, request.Year);
                string tuner = ForzaTuneIdentity.TunerKey(ForzaTuneIdentity.ParseTuners(request.TuneRaw));
                string sourceIdentity;
                string? sourceFingerprint;

                bool hasSrc = !string.IsNullOrEmpty(request.SourceFileId);
                if (hasSrc)
                {
                    sourceIdentity = ForzaTuneIdentity.BuildSourceIdentity(request, modelKey, request.SourceFileId!);
                    sourceFingerprint = ForzaTuneIdentity.BuildFingerprint(request, modelKey);
                }
                else
                {
                    sourceIdentity = "";
                    sourceFingerprint = null;
                }

                string tuneStatus = request.TuneStatus ?? "正常";
                int isManual = request.IsManual ? 1 : 0;

                sql.Parameters["@vehicleId"] = request.VehicleId ?? 0;
                sql.Parameters["@class"] = request.Class;
                sql.Parameters["@matchType"] = request.MatchType;
                sql.Parameters["@drivetrain"] = request.Drivetrain ?? (object)DBNull.Value;
                sql.Parameters["@perfPref"] = request.PerformancePreference ?? (object)DBNull.Value;
                sql.Parameters["@tireCompound"] = request.TireCompound ?? (object)DBNull.Value;
                sql.Parameters["@surfCond"] = request.SurfaceCondition ?? (object)DBNull.Value;
                sql.Parameters["@strength"] = request.Strength ?? (object)DBNull.Value;
                sql.Parameters["@tuner"] = string.IsNullOrEmpty(tuner) ? (object)DBNull.Value : tuner;
                sql.Parameters["@tuneRaw"] = request.TuneRaw ?? (object)DBNull.Value;
                sql.Parameters["@remarks"] = request.Remarks ?? (object)DBNull.Value;
                sql.Parameters["@lapTime"] = request.LapTime ?? (object)DBNull.Value;
                sql.Parameters["@extData"] = request.ExtData ?? (object)DBNull.Value;
                sql.Parameters["@sourceFileId"] = request.SourceFileId ?? "";
                sql.Parameters["@sourceSheet"] = request.SourceSheet ?? (object)DBNull.Value;
                sql.Parameters["@sourceRow"] = request.SourceRow.HasValue ? request.SourceRow.Value : (object)DBNull.Value;
                sql.Parameters["@sourceDisplayName"] = request.SourceDisplayName ?? (object)DBNull.Value;
                sql.Parameters["@sourceIdentity"] = sourceIdentity;
                sql.Parameters["@sourceIdentitySeq"] = seq;
                sql.Parameters["@sourceFingerprint"] = sourceFingerprint ?? (object)DBNull.Value;
                sql.Parameters["@isManual"] = isManual;
                sql.Parameters["@tuneStatus"] = tuneStatus;
                sql.Parameters["@userId"] = request.LastUpdatedUserId;

                sql.Execute(@"
                    INSERT INTO VehicleTunes
                        (VehicleId, Class, MatchType, Drivetrain, PerformancePreference,
                         TireCompound, SurfaceCondition, Strength, Tuner, TuneRaw,
                         Remarks, LapTime, ExtData,
                         SourceFileId, SourceSheet, SourceRow, SourceDisplayName,
                         SourceIdentity, SourceIdentitySeq, SourceFingerprint,
                         IsManual, TuneStatus, LastUpdatedUserId)
                    VALUES
                        (@vehicleId, @class, @matchType, @drivetrain, @perfPref,
                         @tireCompound, @surfCond, @strength, @tuner, @tuneRaw,
                         @remarks, @lapTime, @extData,
                         @sourceFileId, @sourceSheet, @sourceRow, @sourceDisplayName,
                         @sourceIdentity, @sourceIdentitySeq, @sourceFingerprint,
                         @isManual, @tuneStatus, @userId)");

                if (!sql.Success)
                {
                    error = "插入调校失败，请稍后重试。";
                    return 0;
                }

                return (int)sql.LastInsertId;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "InsertTune error: {Message}", ex.Message);
                error = $"插入调校异常：{ex.Message}";
                return 0;
            }
        }

        /// <summary>
        /// 管理员编辑调校显示值：更新可编辑列（Class、MatchType、Drivetrain、PerformancePreference、
        /// TireCompound、SurfaceCondition、Strength、TuneRaw、Remarks、LapTime、ExtData），
        /// <b>冻结</b> SourceIdentity 与 SourceFingerprint（不出现在 SET 列表中）。
        /// 未显式给 TuneStatus 时，回写为"正常"。
        /// 更新后清除该调校的 VehicleTuneCache 行。
        /// </summary>
        public bool UpdateTune(VehicleTuneRequest request, long actorUserId, out string error)
        {
            error = "";

            if (!request.Id.HasValue || request.Id.Value <= 0)
            {
                error = "请提供调校ID。";
                return false;
            }

            try
            {
                sql.NewTransaction();

                // 确认记录存在（加行锁）
                sql.Parameters["@id"] = request.Id.Value;
                sql.ExecuteDataSetForUpdate("SELECT Id FROM VehicleTunes WHERE Id = @id");
                if (!sql.Success || sql.DataSet is null || sql.DataSet.Tables.Count == 0
                    || sql.DataSet.Tables[0].Rows.Count == 0)
                {
                    error = "未找到对应的调校记录。";
                    return false;
                }

                // 重新计算 Tuner
                string tuner = ForzaTuneIdentity.TunerKey(ForzaTuneIdentity.ParseTuners(request.TuneRaw));
                string tuneStatus = string.IsNullOrEmpty(request.TuneStatus) ? "正常" : request.TuneStatus;

                // 决策15：编辑显示字段时置位 IsManual=1（让后续同步走缓存路径）；
                // 仅改调校状态（TuneStatus）不置位——避免状态调整误标记来源调校为人手维护。
                bool changesDisplayField =
                    request.Class != null ||
                    request.MatchType != null ||
                    request.Drivetrain != null ||
                    request.PerformancePreference != null ||
                    request.TireCompound != null ||
                    request.SurfaceCondition != null ||
                    request.Strength != null ||
                    request.TuneRaw != null ||
                    request.Remarks != null ||
                    request.LapTime != null ||
                    request.ExtData != null;

                List<string> setClauses = [];
                // 可编辑列（不含 SourceIdentity / SourceFingerprint）
                AddSetClause(setClauses, "Class", request.Class);
                AddSetClause(setClauses, "MatchType", request.MatchType);
                AddSetClause(setClauses, "Drivetrain", request.Drivetrain);
                AddSetClause(setClauses, "PerformancePreference", request.PerformancePreference);
                AddSetClause(setClauses, "TireCompound", request.TireCompound);
                AddSetClause(setClauses, "SurfaceCondition", request.SurfaceCondition);
                AddSetClause(setClauses, "Strength", request.Strength);
                AddSetClause(setClauses, "LapTime", request.LapTime);
                AddSetClause(setClauses, "Remarks", request.Remarks);
                AddSetClause(setClauses, "ExtData", request.ExtData);
                // TuneRaw + 派生的 Tuner 同步更新；TuneRaw 未提供时两列均不写入，避免置空已有值
                if (request.TuneRaw != null)
                {
                    sql.Parameters["@TuneRaw"] = request.TuneRaw;
                    setClauses.Add("TuneRaw = @TuneRaw");
                    sql.Parameters["@Tuner"] = string.IsNullOrEmpty(tuner) ? (object)DBNull.Value : tuner;
                    setClauses.Add("Tuner = @Tuner");
                }

                // 固定更新的列
                sql.Parameters["@TuneStatus"] = tuneStatus;
                sql.Parameters["@LastUpdatedUserId"] = actorUserId;
                sql.Parameters["@LastUpdatedAt"] = DateTime.UtcNow;
                setClauses.Add("TuneStatus = @TuneStatus");
                setClauses.Add("LastUpdatedUserId = @LastUpdatedUserId");
                setClauses.Add("LastUpdatedAt = @LastUpdatedAt");

                // 决策15：只改显示字段时才置位 IsManual
                if (changesDisplayField)
                {
                    sql.Parameters["@IsManual"] = 1;
                    setClauses.Add("IsManual = @IsManual");
                }

                sql.Parameters["@id"] = request.Id.Value;
                sql.Execute($"UPDATE VehicleTunes SET {string.Join(", ", setClauses)} WHERE Id = @id");
                if (!sql.Success)
                {
                    error = "更新调校失败。";
                    return false;
                }

                // 清除该调校的缓存行
                sql.Parameters["@cacheId"] = request.Id.Value;
                sql.Execute("DELETE FROM VehicleTuneCache WHERE TuneId = @cacheId");
                // 缓存删除失败不影响主更新成功状态，仅记录日志
                if (!sql.Success)
                    logger.LogWarning("UpdateTune: 清除 VehicleTuneCache 行失败，TuneId={Id}", request.Id.Value);

                sql.Commit();
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "UpdateTune error: {Message}", ex.Message);
                error = $"更新调校异常：{ex.Message}";
                return false;
            }
            finally
            {
                sql.Rollback();
            }
        }

        /// <summary>
        /// 按主键列表批量删除调校记录，同时清除对应的 VehicleTuneCache 行。
        /// 返回 (success, message, deletedCount)；deletedCount 为 VehicleTunes 的实际影响行数。
        /// </summary>
        public (bool success, string message, int deletedCount) DeleteTunesByIds(List<int> ids)
        {
            if (ids == null || ids.Count == 0)
                return (false, "未提供有效的调校ID", 0);

            try
            {
                sql.NewTransaction();

                string inClause = string.Join(",", ids.Select((_, i) => $"@id{i}"));
                for (int i = 0; i < ids.Count; i++)
                    sql.Parameters[$"@id{i}"] = ids[i];

                // 先删缓存（参数复用，ClearParametersAfterExecute 默认 true，所以缓存删除用独立参数）
                string cacheInClause = string.Join(",", ids.Select((_, i) => $"@cid{i}"));
                for (int i = 0; i < ids.Count; i++)
                    sql.Parameters[$"@cid{i}"] = ids[i];

                sql.ClearParametersAfterExecute = false;
                sql.Execute($"DELETE FROM VehicleTuneCache WHERE TuneId IN ({cacheInClause})");
                sql.ClearParametersAfterExecute = true;

                // 再删主表
                sql.Execute($"DELETE FROM VehicleTunes WHERE Id IN ({inClause})");
                if (!sql.Success)
                    return (false, "删除调校失败，请稍后重试", 0);

                int deleted = sql.AffectedRows;
                sql.Commit();
                return (true, $"成功删除 {deleted} 条调校", deleted);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "DeleteTunesByIds error");
                return (false, $"删除异常：{ex.Message}", 0);
            }
            finally
            {
                sql.Rollback();
            }
        }

        /// <summary>
        /// 按车型 ID 查询该车型下的所有调校记录，按 Id 升序返回，不分页。
        /// 返回形态与 GetVehicleModelById 一致：(success, message, DataTable?, totalPages)；
        /// totalPages 固定为 1（车型精确匹配，无需分页）。
        /// </summary>
        public (bool success, string message, DataTable? data, int totalPages) GetTunesByVehicleId(int vehicleId)
        {
            sql.Parameters["@vid"] = vehicleId;
            sql.ExecuteDataSet("SELECT * FROM VehicleTunes WHERE VehicleId = @vid ORDER BY Id");
            if (!sql.Success || sql.DataSet is null || sql.DataSet.Tables.Count == 0
                || sql.DataSet.Tables[0].Rows.Count == 0)
                return (false, "未找到该车型的调校记录。", null, 0);
            return (true, "success", sql.DataSet.Tables[0], 1);
        }

        /// <summary>
        /// 按状态分页查询调校记录（镜像 GetVehicleDataTable 的分页风格）。
        /// status 对应 TuneStatus 列（如"正常"/"存疑"/"弃用"）。
        /// </summary>
        public (bool success, string message, DataTable? data, int totalPages) ListTunesByStatus(string status, int page, int pageSize = 12)
        {
            if (page < 1) page = 1;
            int offset = (page - 1) * pageSize;

            sql.Parameters["@status"] = status;

            // 参数传不过去 COUNT 查询（ClearParametersAfterExecute 默认 true），临时关闭
            sql.ClearParametersAfterExecute = false;
            sql.ExecuteDataSet($@"
                SELECT * FROM VehicleTunes
                WHERE TuneStatus = @status
                ORDER BY Id
                LIMIT {offset}, {pageSize}");
            sql.ClearParametersAfterExecute = true;

            if (sql.DataSet is null || sql.DataSet.Tables.Count == 0 || sql.DataSet.Tables[0].Rows.Count == 0)
                return (false, "未找到符合条件的调校记录。", null, 0);

            DataTable dt = sql.DataSet.Tables[0];

            DataRow? countRow = sql.ExecuteDataRow("SELECT COUNT(*) FROM VehicleTunes WHERE TuneStatus = @status");
            int totalCount = 0;
            if (sql.Success && countRow != null)
                _ = int.TryParse(countRow[0].ToString(), out totalCount);

            int totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
            return (true, "success", dt, totalPages);
        }

        public class VehicleMatch
        {
            public int Id { get; set; }
            public string DisplayName { get; set; } = "";
        }

        // ==================== 赛道查询 ====================

        public (bool success, string message, DataTable? data, int totalPages) GetMapDataTable(string whereClause, int page, int pageSize = 12)
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
        /// 按关键字搜索赛道：模糊匹配中文名、英文名、别名（JSON 文本 LIKE）。
        /// 别名是 JSON 数组（如 ["海岸冲刺","海滨直道"]），LIKE 匹配可命中数组内任意成员。
        /// </summary>
        public (bool success, string message, DataTable? data, int totalPages) SearchMaps(string keyword, int page)
        {
            if (string.IsNullOrWhiteSpace(keyword))
                return (false, "请提供赛道名称关键字。", null, 0);

            sql.Parameters["@search"] = $"%{EscapeLikeWildcards(keyword)}%";

            string whereClause = @"
                WHERE NameZh LIKE @search
                   OR NameEn LIKE @search
                   OR Aliases LIKE @search";

            return GetMapDataTable(whereClause, page);
        }

        // ==================== 赛道写操作 ====================

        public bool AddMap(MapRequest request, out string error)
        {
            error = "";

            if (string.IsNullOrEmpty(request.NameZh))
            {
                error = "中文名不能为空。";
                return false;
            }
            if (string.IsNullOrEmpty(request.NameEn))
            {
                error = "英文名不能为空。";
                return false;
            }
            if (string.IsNullOrEmpty(request.RouteType))
            {
                error = "路线类型不能为空。";
                return false;
            }
            if (string.IsNullOrEmpty(request.LengthKm) || !decimal.TryParse(request.LengthKm, out _))
            {
                error = "赛道长度不能为空，且必须为数字。";
                return false;
            }
            if (!ValidateNumericFields(request, out error))
                return false;

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
                AddField(columns, values, "Aliases", EncodeAliases(request.Aliases));
                AddField(columns, values, "LengthKm", request.LengthKm);
                AddField(columns, values, "RouteType", request.RouteType);
                AddField(columns, values, "Surface", request.Surface);
                AddField(columns, values, "RaceCategory", request.RaceCategory ?? "road");
                AddField(columns, values, "Characteristic", request.Characteristic);
                AddField(columns, values, "Difficulty", request.Difficulty);
                AddField(columns, values, "Elevation", request.Elevation);
                AddField(columns, values, "TurnCount", request.TurnCount);
                AddField(columns, values, "RoadWidth", request.RoadWidth);
                AddField(columns, values, "AvgTimeSec", request.AvgTimeSec);
                AddField(columns, values, "WorldRecord", request.WorldRecord);
                AddField(columns, values, "WrVehicle", request.WrVehicle);
                AddField(columns, values, "WrTuning", request.WrTuning);
                AddField(columns, values, "CarClassBest", EncodeAliases(request.CarClassBest));
                AddField(columns, values, "CarTypeBest", EncodeAliases(request.CarTypeBest));
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
            if (!ValidateNumericFields(request, out error))
                return false;
            try
            {
                sql.NewTransaction();

                if (!request.Id.HasValue || request.Id.Value <= 0)
                {
                    error = "请提供赛道ID以定位赛道。";
                    return false;
                }

                sql.Parameters["@id"] = request.Id.Value;
                sql.ExecuteDataSetForUpdate("SELECT Id FROM Maps WHERE Id = @id");

                if (!sql.Success || sql.DataSet.Tables[0].Rows.Count == 0)
                {
                    error = "未找到对应的赛道。请检查ID是否正确。";
                    return false;
                }

                List<string> setClauses = [];
                AddSetClause(setClauses, "NameZh", request.NameZh);
                AddSetClause(setClauses, "NameEn", request.NameEn);
                AddSetClause(setClauses, "Aliases", EncodeAliases(request.Aliases));
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
                AddSetClause(setClauses, "CarClassBest", EncodeAliases(request.CarClassBest));
                AddSetClause(setClauses, "CarTypeBest", EncodeAliases(request.CarTypeBest));
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
                sql.Parameters["@id"] = request.Id.Value;
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

                int deleted = sql.AffectedRows;
                sql.Commit();
                return (true, $"成功删除 {deleted} 条赛道", deleted);
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

                if (whereClauses.Count == 0)
                    return (false, "请提供至少一个匹配条件。", null);

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

        // ==================== 赛道呈现 ====================

        private static readonly Dictionary<string, string> RouteTypeLabel = new()
        {
            ["circuit"] = "圈图",
            ["sprint"] = "冲刺图"
        };
        private static readonly Dictionary<string, string> RaceCategoryLabel = new()
        {
            ["road"] = "公路",
            ["dirt"] = "泥地",
            ["cross_country"] = "越野",
            ["street"] = "街头",
            ["drag"] = "直线加速",
            ["rally"] = "拉力",
            ["showcase"] = "表演赛",
            ["mountain_road"] = "峠道"
        };
        private static readonly Dictionary<string, string> SurfaceLabel = new()
        {
            ["asphalt"] = "柏油路",
            ["dirt"] = "泥地",
            ["gravel"] = "砂石",
            ["sand"] = "沙地",
            ["mixed"] = "混合",
            ["snow"] = "雪地",
            ["cross_country"] = "越野"
        };
        private static readonly Dictionary<string, string> CharacteristicLabel = new()
        {
            ["handling"] = "操控",
            ["speed"] = "极速",
            ["balanced"] = "均衡",
            ["offroad"] = "越野",
            ["drift"] = "漂移",
            ["technical"] = "技术"
        };
        private static readonly Dictionary<string, string> ElevationLabel = new()
        {
            ["flat"] = "平坦",
            ["gentle"] = "缓坡",
            ["hilly"] = "丘陵",
            ["mountainous"] = "山地"
        };
        private static readonly Dictionary<string, string> RoadWidthLabel = new()
        {
            ["narrow"] = "窄",
            ["normal"] = "正常",
            ["wide"] = "宽"
        };
        private static readonly Dictionary<string, string> SeasonLabel = new()
        {
            ["spring"] = "春",
            ["summer"] = "夏",
            ["autumn"] = "秋",
            ["winter"] = "冬",
            ["wet"] = "湿地",
            ["dry"] = "干地",
            ["all"] = "全部"
        };
        private static readonly Dictionary<string, string> WeatherLabel = new()
        {
            ["clear"] = "晴",
            ["rain"] = "雨",
            ["fog"] = "雾",
            ["storm"] = "暴风雨",
            ["snow"] = "雪",
            ["variable"] = "多变"
        };

        private static string T(Dictionary<string, string> map, string key)
            => map.TryGetValue(key, out string? v) ? v : key;

        /// <summary>去掉 decimal 末尾多余的零：6.900 → 6.9，8.0 → 8</summary>
        private static string FormatKm(string raw)
        {
            if (decimal.TryParse(raw, out decimal d))
                return d.ToString("0.###");
            return raw;
        }

        internal static string RenderMapsAsCards(DataTable dt)
        {
            StringBuilder sb = new();
            for (int i = 0; i < dt.Rows.Count; i++)
            {
                DataRow row = dt.Rows[i];
                string Cell(string col) => dt.Columns.Contains(col) ? (row[col]?.ToString() ?? "").Trim() : "";

                // 标题行：中文名 (英文名) · 冲刺图/柏油路 · 操控
                string title = $"**{Cell("NameZh")}**";
                if (Cell("NameEn") != "") title += $" ({Cell("NameEn")})";

                List<string> titleTags = [];
                string rt = Cell("RouteType");
                if (rt != "") titleTags.Add(T(RouteTypeLabel, rt));
                string sf = Cell("Surface");
                if (sf != "") titleTags.Add(T(SurfaceLabel, sf));
                string ch = Cell("Characteristic");
                if (ch != "") titleTags.Add(T(CharacteristicLabel, ch));
                if (Cell("IsReverse") == "1") titleTags.Add("反向");
                if (Cell("NightMode") == "1") titleTags.Add("夜间");
                if (titleTags.Count > 0)
                    title += " · " + string.Join(" · ", titleTags);

                title += "\r\n> ";
                sb.Append(title);

                // 长度 | 难度 | 海拔 | 弯道
                List<string> stats = [];
                if (Cell("LengthKm") != "") stats.Add($"全长 {FormatKm(Cell("LengthKm"))} km");
                if (Cell("Difficulty") != "") stats.Add($"难度 {Cell("Difficulty")}/5");
                string elev = Cell("Elevation");
                if (elev != "") stats.Add(T(ElevationLabel, elev));
                if (Cell("TurnCount") != "") stats.Add($"{Cell("TurnCount")} 弯");
                AppendLineIfAny(sb, string.Join(" | ", stats));

                // 赛事大类 | 道路宽度
                List<string> extra = [];
                string rc = Cell("RaceCategory");
                if (rc != "") extra.Add(T(RaceCategoryLabel, rc));
                string rw = Cell("RoadWidth");
                if (rw != "") extra.Add($"路宽：{T(RoadWidthLabel, rw)}");
                AppendLineIfAny(sb, string.Join(" · ", extra));

                // 推荐车辆等级 / 类型
                List<string> recommends = [];
                if (Cell("CarClassBest") != "") recommends.Add($"等级推荐：{Cell("CarClassBest")}");
                if (Cell("CarTypeBest") != "") recommends.Add($"车型推荐：{Cell("CarTypeBest")}");
                AppendLineIfAny(sb, string.Join(" | ", recommends));

                // 最佳赛季 | 默认天气
                List<string> env = [];
                string sb2 = Cell("SeasonBest");
                if (sb2 != "") env.Add($"最佳赛季：{T(SeasonLabel, sb2)}");
                string wd = Cell("WeatherDefault");
                if (wd != "") env.Add($"天气：{T(WeatherLabel, wd)}");
                AppendLineIfAny(sb, string.Join(" · ", env));

                // 区域 | DLC
                List<string> loc = [];
                if (Cell("Region") != "") loc.Add(Cell("Region"));
                if (Cell("Dlc") != "") loc.Add($"DLC：{Cell("Dlc")}");
                AppendLineIfAny(sb, string.Join(" · ", loc));

                // 世界纪录
                if (Cell("WorldRecord") != "")
                {
                    string wrLine = $"🏆 世界纪录：{Cell("WorldRecord")}";
                    if (Cell("WrVehicle") != "") wrLine += $" · {Cell("WrVehicle")}";
                    AppendLineIfAny(sb, wrLine);
                }

                // 备注
                AppendLineIfAny(sb, Prefix("备注：", Cell("Notes")));

                if (i < dt.Rows.Count - 1) sb.AppendLine("\r\n");
            }
            return sb.ToString();
        }

        public class MapMatch
        {
            public int Id { get; set; }
            public string DisplayName { get; set; } = "";
        }
    }
}
