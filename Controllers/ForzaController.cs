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

        [AllowAnonymous]
        [HttpGet("vehicles/{id}")]
        public IActionResult GetVehicleById(int id)
        {
            var (success, _, data, _) = forzaService.GetVehicleById(id);
            if (!success || data == null || data.Rows.Count == 0)
                return NotFound(new { success = false, message = $"未找到 ID 为 {id} 的车辆" });

            var vehicle = ConvertDataRowToVehicle(data.Rows[0]);
            return Ok(new { success = true, vehicle });
        }

        [Authorize(Roles = "admin,op")]
        [HttpPost("vehicles")]
        public IActionResult AddVehicle([FromBody] VehicleRequest request)
        {
            if (request == null)
                return BadRequest(new { success = false, message = "请求体不能为空" });

            bool result = forzaService.AddVehicle(request, out string error);
            if (result)
                return Ok(new { success = true, message = "车辆新增成功" });
            else
                return BadRequest(new { success = false, message = error });
        }

        [Authorize(Roles = "admin,op")]
        [HttpPut("vehicles/{id}")]
        public IActionResult UpdateVehicle(int id, [FromBody] VehicleRequest request)
        {
            if (request == null)
                return BadRequest(new { success = false, message = "请求体不能为空" });

            request.Id = id;
            bool result = forzaService.UpdateVehicle(request, out string error);
            if (result)
                return Ok(new { success = true, message = "车辆修改成功" });
            else
                return BadRequest(new { success = false, message = error });
        }

        [Authorize(Roles = "admin,op")]
        [HttpDelete("vehicles/{id}")]
        public IActionResult DeleteVehicle(int id)
        {
            var (success, message, deletedCount) = forzaService.DeleteVehiclesByIds([id]);
            if (success && deletedCount > 0)
                return Ok(new { success = true, message = $"已删除车辆 ID {id}" });
            else
                return NotFound(new { success = false, message });
        }

        [Authorize(Roles = "admin,op")]
        [HttpDelete("vehicles")]
        public IActionResult DeleteVehicles([FromQuery] string ids)
        {
            if (string.IsNullOrWhiteSpace(ids))
                return BadRequest(new { success = false, message = "请提供 ids 参数，例如 ?ids=1,2,3" });

            List<int> idList = [.. ids.Split(',').Where(s => int.TryParse(s, out _)).Select(int.Parse)];
            if (idList.Count == 0)
                return BadRequest(new { success = false, message = "ids 中未包含有效的数字 ID" });

            var (success, message, deletedCount) = forzaService.DeleteVehiclesByIds(idList);
            if (success)
                return Ok(new { success = true, message = $"成功删除 {deletedCount} 辆车" });
            else
                return BadRequest(new { success = false });
        }

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
                return Ok(new { success = false, message = message ?? "未找到匹配车辆", matches = Array.Empty<object>() });

            return Ok(new { success = true, message = $"找到 {matches.Count} 辆匹配车辆", matches });
        }

        private static List<object> ConvertDataTableToVehicles(DataTable dt)
        {
            List<object> list = [];
            foreach (DataRow row in dt.Rows)
            {
                list.Add(ConvertDataRowToVehicle(row));
            }
            return list;
        }

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
            if (success && deletedCount > 0)
                return Ok(new { success = true, message = $"已删除赛道 ID {id}" });
            else
                return NotFound(new { success = false });
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
            if (success)
                return Ok(new { success = true, message = $"成功删除 {deletedCount} 条赛道" });
            else
                return BadRequest(new { success = false });
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
