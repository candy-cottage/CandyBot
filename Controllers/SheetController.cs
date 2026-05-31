using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Services.BotServices;
using Microsoft.AspNetCore.Mvc;

namespace CandyCottage.CandyBot.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class SheetController(ILogger<SheetController> logger, SheetService service) : Controller
    {
        [HttpGet("view")]
        public async Task<IActionResult> Viewer([FromQuery] string fileId = "", [FromQuery] string title = "")
        {
            SheetViewModel model = new()
            {
                FileId = fileId,
                CurrentTitle = title
            };

            if (string.IsNullOrEmpty(fileId))
            {
                return View(model);
            }

            // 1. 获取文档元数据
            SheetInfoResponse fileInfo = await service.GetFileDataAsync(fileId, model.Messages);
            if (fileInfo.Code != 0 || fileInfo.Properties == null || fileInfo.Properties.Count == 0)
            {
                model.Messages["error"] = $"获取文档信息失败：{fileInfo.Message ?? "无有效工作表"}";
                return View(model);
            }

            // 提取所有工作表标题
            model.SheetTitles = [.. fileInfo.Properties.Where(p => !string.IsNullOrEmpty(p.Title)).Select(p => p.Title!)];

            if (model.SheetTitles.Count == 0)
            {
                model.Messages["error"] = "文档中未找到任何工作表";
                return View(model);
            }

            // 2. 确定当前要显示的工作表
            if (string.IsNullOrEmpty(title) || !model.SheetTitles.Contains(title))
            {
                model.CurrentTitle = model.SheetTitles.First();
            }

            // 3. 获取指定工作表的数据
            Dictionary<string, List<List<string>>> allSheetsData = await service.GetSheetDataAsync(fileId, model.CurrentTitle, model.Messages);

            // 注意：GetSheetDataAsync 当传入 title 时，返回的字典只包含该 title
            if (allSheetsData.TryGetValue(model.CurrentTitle, out List<List<string>>? grid))
            {
                model.CurrentGrid = grid ?? [];
            }
            else
            {
                model.Messages[model.CurrentTitle] = "未能加载该工作表数据";
            }

            return View(model);
        }

        [HttpGet("file/{fileId}")]
        public async Task<IActionResult> GetFileDataByName(string fileId)
        {
            try
            {
                if (string.IsNullOrEmpty(fileId))
                {
                    return BadRequest("fileId 不能为空");
                }

                Dictionary<string, string> messages = [];
                SheetInfoResponse data = await service.GetFileDataAsync(fileId, messages);

                return Ok(new
                {
                    fileId,
                    messages,
                    data
                });
            }
            catch (HttpRequestException ex)
            {
                logger.LogError(ex, "请求腾讯文档 API 失败");
                return StatusCode(502, "与腾讯文档 API 通信失败");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "获取工作表数据时发生错误");
                return StatusCode(500, "服务器内部错误");
            }
        }

        [HttpGet("{fileId}")]
        public async Task<IActionResult> GetSheetDataByName(string fileId, [FromQuery] string title = "")
        {
            try
            {
                if (string.IsNullOrEmpty(fileId))
                {
                    return BadRequest("fileId 不能为空");
                }

                Dictionary<string, string> messages = [];
                Dictionary<string, List<List<string>>> data = await service.GetSheetDataAsync(fileId, title, messages);

                return Ok(new
                {
                    fileId,
                    title,
                    messages,
                    data
                });
            }
            catch (HttpRequestException ex)
            {
                logger.LogError(ex, "请求腾讯文档 API 失败");
                return StatusCode(502, "与腾讯文档 API 通信失败");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "获取工作表数据时发生错误");
                return StatusCode(500, "服务器内部错误");
            }
        }
    }
}
