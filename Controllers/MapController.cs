using System.Data;
using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CandyCottage.CandyBot.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class MapController(MapService mapService) : ControllerBase
    {
        [AllowAnonymous]
        [HttpGet("maps")]
        public IActionResult SearchMaps([FromQuery] string? keyword, [FromQuery] int page = 1)
        {
            if (string.IsNullOrWhiteSpace(keyword))
                return BadRequest(new { success = false, message = "请提供 keyword 参数进行搜索" });

            var (success, msg, data, totalPages) = mapService.SearchMaps(keyword, page);
            if (!success || data == null)
                return Ok(new { success = false, message = msg, maps = Array.Empty<object>() });

            List<object> maps = ConvertDataTableToMaps(data);
            return Ok(new { success = true, page, totalPages, maps });
        }

        [AllowAnonymous]
        [HttpGet("maps/{id}")]
        public IActionResult GetMapById(int id)
        {
            var (success, _, data, _) = mapService.GetMapById(id);
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

            bool result = mapService.AddMap(request, out string error);
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
            bool result = mapService.UpdateMap(request, out string error);
            if (result)
                return Ok(new { success = true, message = "赛道修改成功" });
            else
                return BadRequest(new { success = false, message = error });
        }

        [Authorize(Roles = "admin,op")]
        [HttpDelete("maps/{id}")]
        public IActionResult DeleteMap(int id)
        {
            var (success, message, deletedCount) = mapService.DeleteMapsByIds([id]);
            if (success && deletedCount > 0)
                return Ok(new { success = true, message = $"已删除赛道 ID {id}" });
            else
                return NotFound(new { success = false, message });
        }

        [Authorize(Roles = "admin,op")]
        [HttpDelete("maps")]
        public IActionResult DeleteMaps([FromQuery] string ids)
        {
            if (string.IsNullOrWhiteSpace(ids))
                return BadRequest(new { success = false, message = "请提供 ids 参数，例如 ?ids=1,2,3" });

            List<int> idList = [.. ids.Split(',').Select(s => int.TryParse(s, out int id) ? id : -1).Select(id => id)];
            if (idList.Count == 0)
                return BadRequest(new { success = false, message = "ids 中未包含有效的数字 ID" });

            var (success, message, deletedCount) = mapService.DeleteMapsByIds(idList);
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
            var (success, message, matches) = mapService.FindMapsByCriteria(request);
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
