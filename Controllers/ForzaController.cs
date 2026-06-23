using System.Data;
using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CandyCottage.CandyBot.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class ForzaController(ForzaService forzaService) : ControllerBase
    {
        // ==================== 车辆搜索端点（保持向后兼容，按调校行分页） ====================

        [AllowAnonymous]
        [HttpGet("vehicles")]
        public IActionResult GetVehicles([FromQuery] string? name, [FromQuery] string? classType, [FromQuery] string? matchType, [FromQuery] int page = 1)
        {
            if (!string.IsNullOrEmpty(name))
            {
                var (success, msg, data, totalPages) = forzaService.GetVehicleInfoRaw(name, page);
                if (!success || data == null)
                    return Ok(new { success = false, message = msg, vehicles = Array.Empty<object>() });

                List<object> vehicles = ConvertDataTableToVehicles(data);
                return Ok(new { success = true, page, totalPages, vehicles });
            }
            else if (!string.IsNullOrEmpty(classType) || !string.IsNullOrEmpty(matchType))
            {
                var (success, msg, data, totalPages) = forzaService.GetVehiclesByClassAndMatchTypeRaw(classType, matchType, page);
                if (!success || data == null)
                    return Ok(new { success = false, message = msg, vehicles = Array.Empty<object>() });

                List<object> vehicles = ConvertDataTableToVehicles(data);
                return Ok(new { success = true, page, totalPages, vehicles });
            }
            else
            {
                return BadRequest(new { success = false, message = "请提供查询参数：name 或 (classType/matchType)" });
            }
        }

        // ==================== 车型（VehicleId）端点 ====================

        /// <summary>
        /// 查询指定车型及其全部调校，id 为 VehicleId（Vehicles.Id 主键）。
        /// 响应：{ success, vehicle: {...}, tunes: [...] }；车型不存在时 404。
        /// </summary>
        [AllowAnonymous]
        [HttpGet("vehicles/{id:int}")]
        public IActionResult GetVehicleById(int id)
        {
            var (vSuccess, _, vData, _) = forzaService.GetVehicleModelById(id);
            if (!vSuccess || vData == null || vData.Rows.Count == 0)
                return NotFound(new { success = false, message = $"未找到 ID 为 {id} 的车型" });

            object vehicle = ConvertDataRowToVehicleModel(vData.Rows[0]);

            // 调校列表：无调校时返回空数组，而非 404（车型可以暂无调校）
            var (tSuccess, _, tData, _) = forzaService.GetTunesByVehicleId(id);
            List<object> tunes = tSuccess && tData != null
                ? ConvertDataTableToTunes(tData)
                : [];

            return Ok(new { success = true, vehicle, tunes });
        }

        /// <summary>
        /// 新增或幂等取回车型；id 参数不适用（由 ModelKey 唯一决定）。
        /// 响应：{ success, vehicleId }
        /// </summary>
        [Authorize(Roles = "admin,op")]
        [HttpPost("vehicles")]
        public IActionResult AddVehicleModel([FromBody] VehicleModelRequest request)
        {
            if (request == null)
                return BadRequest(new { success = false, message = "请求体不能为空" });

            // actorUserId：从 JWT Claims 取，此处简化为常量（调用方凭 Bearer token 已鉴权）
            int vehicleId = forzaService.EnsureVehicleModel(request, actorUserId: 0, out string error);
            if (vehicleId == 0)
                return BadRequest(new { success = false, message = error });

            return Ok(new { success = true, vehicleId });
        }

        /// <summary>更新车型的 Manufacturer/Model/Year。id 为 VehicleId。</summary>
        [Authorize(Roles = "admin,op")]
        [HttpPut("vehicles/{id:int}")]
        public IActionResult UpdateVehicleModel(int id, [FromBody] VehicleModelRequest request)
        {
            if (request == null)
                return BadRequest(new { success = false, message = "请求体不能为空" });

            request.Id = id;
            bool result = forzaService.UpdateVehicleModel(request, actorUserId: 0, out string error);
            if (result)
                return Ok(new { success = true, message = "车型修改成功" });
            else
                return BadRequest(new { success = false, message = error });
        }

        /// <summary>
        /// 删除单个车型（及其全部调校，由 FK CASCADE 处理）。id 为 VehicleId。
        /// </summary>
        [Authorize(Roles = "admin,op")]
        [HttpDelete("vehicles/{id:int}")]
        public IActionResult DeleteVehicleModel(int id)
        {
            var (success, message, deletedCount) = forzaService.DeleteVehicleModelsByIds([id]);
            if (!success)
                return BadRequest(new { success = false, message });
            if (deletedCount == 0)
                return NotFound(new { success = false, message });
            return Ok(new { success = true, message = $"已删除车型 ID {id}（及其全部关联调校）" });
        }

        /// <summary>
        /// 批量删除车型（及各自全部调校，由 FK CASCADE 处理）。?ids=1,2,3
        /// </summary>
        [Authorize(Roles = "admin,op")]
        [HttpDelete("vehicles")]
        public IActionResult DeleteVehicleModels([FromQuery] string ids)
        {
            if (string.IsNullOrWhiteSpace(ids))
                return BadRequest(new { success = false, message = "请提供 ids 参数，例如 ?ids=1,2,3" });

            List<int> idList = [.. ids.Split(',').Where(s => int.TryParse(s, out _)).Select(int.Parse)];
            if (idList.Count == 0)
                return BadRequest(new { success = false, message = "ids 中未包含有效的数字 ID" });

            var (success, message, deletedCount) = forzaService.DeleteVehicleModelsByIds(idList);
            if (!success)
                return BadRequest(new { success = false, message });
            return Ok(new { success = true, message = $"成功删除 {deletedCount} 个车型（含全部关联调校）" });
        }

        /// <summary>
        /// 按车型属性查找候选车型，返回 VehicleId + DisplayName 列表。
        /// 用于在新增调校前定位目标车型。
        /// </summary>
        [Authorize(Roles = "admin,op")]
        [HttpGet("vehicles/match")]
        public IActionResult MatchVehicles([FromQuery] string manufacturer, [FromQuery] string model, [FromQuery] string year, [FromQuery] string classType, [FromQuery] string matchType)
        {
            VehicleRequest request = new()
            {
                Manufacturer = manufacturer,
                Model = model,
                Year = year,
                Class = classType,
                MatchType = matchType
            };
            var (success, message, matches) = forzaService.FindVehiclesByCriteria(request);
            if (!success || matches == null || matches.Count == 0)
                return Ok(new { success = false, message = message ?? "未找到匹配车型", matches = Array.Empty<object>() });

            return Ok(new { success = true, message = $"找到 {matches.Count} 个匹配车型", matches });
        }

        // ==================== 调校（TuneId）端点 ====================

        /// <summary>
        /// 查询单条调校记录，id 为 TuneId（VehicleTunes.Id 主键）。
        /// 响应：{ success, tuneId, tune: {...} }；调校不存在时 404。
        /// </summary>
        [AllowAnonymous]
        [HttpGet("tunes/{id:int}")]
        public IActionResult GetTuneById(int id)
        {
            var (success, _, row) = forzaService.GetTuneById(id);
            if (!success || row == null)
                return NotFound(new { success = false, message = $"未找到 ID 为 {id} 的调校记录" });

            object tune = ConvertDataRowToTune(row);
            return Ok(new { success = true, tuneId = id, tune });
        }

        /// <summary>
        /// 手动新增调校（IsManual=1）；VehicleId/Class/MatchType/Drivetrain 必填。
        /// 响应：{ success, tuneId }
        /// </summary>
        [Authorize(Roles = "admin,op")]
        [HttpPost("tunes")]
        public IActionResult AddTune([FromBody] VehicleTuneRequest request)
        {
            if (request == null)
                return BadRequest(new { success = false, message = "请求体不能为空" });

            int tuneId = forzaService.AddTune(request, actorUserId: 0, out string error);
            if (tuneId == 0)
                return BadRequest(new { success = false, message = error });

            return Ok(new { success = true, tuneId });
        }

        /// <summary>
        /// 编辑调校显示值（Class、MatchType、Drivetrain 等），冻结 SourceIdentity 和 SourceFingerprint。
        /// id 为 TuneId。
        /// </summary>
        [Authorize(Roles = "admin,op")]
        [HttpPut("tunes/{id:int}")]
        public IActionResult UpdateTune(int id, [FromBody] VehicleTuneRequest request)
        {
            if (request == null)
                return BadRequest(new { success = false, message = "请求体不能为空" });

            request.Id = id;
            bool result = forzaService.UpdateTune(request, actorUserId: 0, out string error);
            if (result)
                return Ok(new { success = true, message = "调校修改成功" });
            else
                return BadRequest(new { success = false, message = error });
        }

        /// <summary>
        /// 删除单条调校，同时清除对应缓存行。id 为 TuneId。
        /// </summary>
        [Authorize(Roles = "admin,op")]
        [HttpDelete("tunes/{id:int}")]
        public IActionResult DeleteTune(int id)
        {
            var (success, message, deletedCount) = forzaService.DeleteTunesByIds([id]);
            if (!success)
                return BadRequest(new { success = false, message });
            if (deletedCount == 0)
                return NotFound(new { success = false, message });
            return Ok(new { success = true, message = $"已删除调校 ID {id}" });
        }

        /// <summary>批量删除调校（同时清除对应缓存行）。?ids=1,2,3</summary>
        [Authorize(Roles = "admin,op")]
        [HttpDelete("tunes")]
        public IActionResult DeleteTunes([FromQuery] string ids)
        {
            if (string.IsNullOrWhiteSpace(ids))
                return BadRequest(new { success = false, message = "请提供 ids 参数，例如 ?ids=1,2,3" });

            List<int> idList = [.. ids.Split(',').Where(s => int.TryParse(s, out _)).Select(int.Parse)];
            if (idList.Count == 0)
                return BadRequest(new { success = false, message = "ids 中未包含有效的数字 ID" });

            var (success, message, deletedCount) = forzaService.DeleteTunesByIds(idList);
            if (!success)
                return BadRequest(new { success = false, message });
            return Ok(new { success = true, message = $"成功删除 {deletedCount} 条调校" });
        }

        // ==================== 赛道端点 ====================

        [AllowAnonymous]
        [HttpGet("maps")]
        public IActionResult SearchMaps([FromQuery] string? keyword, [FromQuery] int page = 1)
        {
            if (string.IsNullOrWhiteSpace(keyword))
                return BadRequest(new { success = false, message = "请提供 keyword 参数进行搜索" });

            var (success, msg, data, totalPages) = forzaService.SearchMaps(keyword, page);
            if (!success || data == null)
                return Ok(new { success = false, message = msg, maps = Array.Empty<object>() });

            List<object> maps = ConvertDataTableToMaps(data);
            return Ok(new { success = true, page, totalPages, maps });
        }

        [AllowAnonymous]
        [HttpGet("maps/{id}")]
        public IActionResult GetMapById(int id)
        {
            var (success, _, data, _) = forzaService.GetMapById(id);
            if (!success || data == null || data.Rows.Count == 0)
                return NotFound(new { success = false, message = $"未找到 ID 为 {id} 的赛道" });

            var map = ConvertDataRowToMap(data.Rows[0]);
            return Ok(new { success = true, map });
        }

        [Authorize(Roles = "admin,op")]
        [HttpPost("maps")]
        public IActionResult AddMap([FromBody] MapRequest request)
        {
            if (request == null)
                return BadRequest(new { success = false, message = "请求体不能为空" });

            bool result = forzaService.AddMap(request, out string error);
            if (result)
                return Ok(new { success = true, message = "赛道新增成功" });
            else
                return BadRequest(new { success = false, message = error });
        }

        [Authorize(Roles = "admin,op")]
        [HttpPut("maps/{id}")]
        public IActionResult UpdateMap(int id, [FromBody] MapRequest request)
        {
            if (request == null)
                return BadRequest(new { success = false, message = "请求体不能为空" });

            request.Id = id;
            bool result = forzaService.UpdateMap(request, out string error);
            if (result)
                return Ok(new { success = true, message = "赛道修改成功" });
            else
                return BadRequest(new { success = false, message = error });
        }

        [Authorize(Roles = "admin,op")]
        [HttpDelete("maps/{id}")]
        public IActionResult DeleteMap(int id)
        {
            var (success, message, deletedCount) = forzaService.DeleteMapsByIds([id]);
            if (!success)
                return BadRequest(new { success = false, message });
            if (deletedCount == 0)
                return NotFound(new { success = false, message });
            return Ok(new { success = true, message = $"已删除赛道 ID {id}" });
        }

        [Authorize(Roles = "admin,op")]
        [HttpDelete("maps")]
        public IActionResult DeleteMaps([FromQuery] string ids)
        {
            if (string.IsNullOrWhiteSpace(ids))
                return BadRequest(new { success = false, message = "请提供 ids 参数，例如 ?ids=1,2,3" });

            List<int> idList = [.. ids.Split(',').Where(s => int.TryParse(s, out _)).Select(int.Parse)];
            if (idList.Count == 0)
                return BadRequest(new { success = false, message = "ids 中未包含有效的数字 ID" });

            var (success, message, deletedCount) = forzaService.DeleteMapsByIds(idList);
            if (!success)
                return BadRequest(new { success = false, message });
            return Ok(new { success = true, message = $"成功删除 {deletedCount} 条赛道" });
        }

        [Authorize(Roles = "admin,op")]
        [HttpGet("maps/match")]
        public IActionResult MatchMaps([FromQuery] string? nameZh, [FromQuery] string? nameEn,
            [FromQuery] string? routeType, [FromQuery] string? surface, [FromQuery] string? raceCategory,
            [FromQuery] string? characteristic, [FromQuery] string? difficulty, [FromQuery] string? region,
            [FromQuery] string? dlc)
        {
            MapRequest request = new()
            {
                NameZh = nameZh,
                NameEn = nameEn,
                RouteType = routeType,
                Surface = surface,
                RaceCategory = raceCategory,
                Characteristic = characteristic,
                Difficulty = difficulty,
                Region = region,
                Dlc = dlc
            };
            var (success, message, matches) = forzaService.FindMapsByCriteria(request);
            if (!success || matches == null || matches.Count == 0)
                return Ok(new { success = false, message = message ?? "未找到匹配赛道", matches = Array.Empty<object>() });

            return Ok(new { success = true, message = $"找到 {matches.Count} 条匹配赛道", matches });
        }

        // ==================== 私有辅助方法 ====================

        private static List<object> ConvertDataTableToVehicles(DataTable dt)
        {
            List<object> list = [];
            foreach (DataRow row in dt.Rows)
            {
                list.Add(ConvertDataRowToVehicle(row));
            }
            return list;
        }

        /// <summary>将 JOIN 查询结果行（含旧别名 Attribute/Tune）转换为响应对象。</summary>
        private static object ConvertDataRowToVehicle(DataRow row)
        {
            return new
            {
                Id = Convert.ToInt32(row["Id"]),
                Class = row["Class"]?.ToString(),
                MatchType = row["MatchType"]?.ToString(),
                Manufacturer = row["Manufacturer"]?.ToString(),
                Model = row["Model"]?.ToString(),
                Year = row["Year"]?.ToString(),
                Drivetrain = row["Drivetrain"]?.ToString(),
                Attribute = row["Attribute"]?.ToString(),
                TireCompound = row["TireCompound"]?.ToString(),
                SurfaceCondition = row["SurfaceCondition"]?.ToString(),
                Strength = row["Strength"]?.ToString(),
                Tune = row["Tune"]?.ToString(),
                Remarks = row["Remarks"]?.ToString(),
                LapTime = row["LapTime"]?.ToString()
            };
        }

        /// <summary>将 Vehicles 表行转换为车型响应对象，显式暴露 vehicleId 字段。</summary>
        private static object ConvertDataRowToVehicleModel(DataRow row)
        {
            return new
            {
                vehicleId = Convert.ToInt32(row["Id"]),
                Manufacturer = row["Manufacturer"]?.ToString(),
                Model = row["Model"]?.ToString(),
                Year = row["Year"]?.ToString(),
                ModelKey = row["ModelKey"]?.ToString(),
            };
        }

        private static List<object> ConvertDataTableToTunes(DataTable dt)
        {
            List<object> list = [];
            foreach (DataRow row in dt.Rows)
                list.Add(ConvertDataRowToTune(row));
            return list;
        }

        /// <summary>将 VehicleTunes 表行转换为调校响应对象，显式暴露 tuneId 字段。</summary>
        private static object ConvertDataRowToTune(DataRow row)
        {
            return new
            {
                tuneId = Convert.ToInt32(row["Id"]),
                vehicleId = row.Table.Columns.Contains("VehicleId")
                    ? Convert.ToInt32(row["VehicleId"])
                    : (object?)null,
                Class = row["Class"]?.ToString(),
                MatchType = row["MatchType"]?.ToString(),
                Drivetrain = row["Drivetrain"]?.ToString(),
                PerformancePreference = row.Table.Columns.Contains("PerformancePreference")
                    ? row["PerformancePreference"]?.ToString()
                    : null,
                TireCompound = row["TireCompound"]?.ToString(),
                SurfaceCondition = row["SurfaceCondition"]?.ToString(),
                Strength = row["Strength"]?.ToString(),
                TuneRaw = row.Table.Columns.Contains("TuneRaw") ? row["TuneRaw"]?.ToString() : null,
                Remarks = row["Remarks"]?.ToString(),
                LapTime = row["LapTime"]?.ToString(),
                TuneStatus = row.Table.Columns.Contains("TuneStatus") ? row["TuneStatus"]?.ToString() : null,
                IsManual = row.Table.Columns.Contains("IsManual")
                    ? Convert.ToInt32(row["IsManual"])
                    : (object?)null,
            };
        }

        private static List<object> ConvertDataTableToMaps(DataTable dt)
        {
            List<object> list = [];
            foreach (DataRow row in dt.Rows)
                list.Add(ConvertDataRowToMap(row));
            return list;
        }

        private static object ConvertDataRowToMap(DataRow row)
        {
            return new
            {
                Id = Convert.ToInt32(row["Id"]),
                NameZh = row["NameZh"]?.ToString(),
                NameEn = row["NameEn"]?.ToString(),
                Aliases = row["Aliases"]?.ToString(),
                LengthKm = row["LengthKm"]?.ToString(),
                RouteType = row["RouteType"]?.ToString(),
                Surface = row["Surface"]?.ToString(),
                RaceCategory = row["RaceCategory"]?.ToString(),
                Characteristic = row["Characteristic"]?.ToString(),
                Difficulty = row["Difficulty"]?.ToString(),
                Elevation = row["Elevation"]?.ToString(),
                TurnCount = row["TurnCount"]?.ToString(),
                RoadWidth = row["RoadWidth"]?.ToString(),
                AvgTimeSec = row["AvgTimeSec"]?.ToString(),
                WorldRecord = row["WorldRecord"]?.ToString(),
                WrVehicle = row["WrVehicle"]?.ToString(),
                WrTuning = row["WrTuning"]?.ToString(),
                CarClassBest = row["CarClassBest"]?.ToString(),
                CarTypeBest = row["CarTypeBest"]?.ToString(),
                Region = row["Region"]?.ToString(),
                SeasonBest = row["SeasonBest"]?.ToString(),
                WeatherDefault = row["WeatherDefault"]?.ToString(),
                NightMode = row["NightMode"]?.ToString(),
                Dlc = row["Dlc"]?.ToString(),
                IsReverse = row["IsReverse"]?.ToString(),
                ImageUrl = row["ImageUrl"]?.ToString(),
                MinimapUrl = row["MinimapUrl"]?.ToString(),
                Notes = row["Notes"]?.ToString()
            };
        }
    }
}
