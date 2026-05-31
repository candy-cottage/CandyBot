using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Utilities;
using Microsoft.Extensions.Caching.Memory;
using Milimoe.FunGame.Core.Api.Utility;

namespace CandyCottage.CandyBot.Services.BotServices
{
    public class SheetService(ILogger<SheetService> logger, IHttpClientFactory httpClientFactory, TencentDocsConfig config, IMemoryCache memoryCache)
    {
        private static readonly Lock _lockSaveFileData = new();
        private static readonly Lock _lockSaveSheetData = new();

        public Dictionary<string, List<List<string>>> LoadSheetDataFromLocalFile(string fileId, string title = "")
        {
            PluginConfig tableConfig = new("sheets", fileId);
            tableConfig.LoadConfig();
            Dictionary<string, List<List<string>>> data = [];
            if (title != "")
            {
                data.Add(title, tableConfig.Get<List<List<string>>>(title) ?? []);
            }
            else
            {
                foreach (string key in tableConfig.Keys)
                {
                    data.Add(key, tableConfig.Get<List<List<string>>>(key) ?? []);
                }
            }
            return data;
        }

        public SheetInfoResponse LoadFileDataFromLocalFile(string fileId)
        {
            PluginConfig fileConfig = new("files", fileId);
            fileConfig.LoadConfig();
            SheetInfoResponse data = fileConfig.Get<SheetInfoResponse>("data") ?? new();
            return data;
        }

        public async Task<SheetInfoResponse> GetFileDataAsync(string fileId, Dictionary<string, string>? messages = null)
        {
            messages ??= [];
            if (memoryCache.TryGetValue($"sheet::{fileId}", out string? time))
            {
                logger.LogDebug("从缓存中获取文档数据");
                messages[fileId] = $"从缓存中获取文档数据。文档 {fileId} 缓存于：{time}";
                SheetInfoResponse result = LoadFileDataFromLocalFile(fileId);
                return result;
            }

            await HandleGetSheetDataAsync(fileId, messages);
            return LoadFileDataFromLocalFile(fileId);
        }


        public async Task<Dictionary<string, List<List<string>>>> GetSheetDataAsync(string fileId, string title = "", Dictionary<string, string>? messages = null)
        {
            messages ??= [];

            if (memoryCache.TryGetValue($"sheet::{fileId}", out string? time))
            {
                logger.LogDebug("从缓存中获取表格数据");
                messages[fileId] = $"从缓存中获取表格数据。文档 {fileId} 缓存于：{time}";
                return LoadSheetDataFromLocalFile(fileId, title);
            }

            await HandleGetSheetDataAsync(fileId, messages);
            return LoadSheetDataFromLocalFile(fileId, title);
        }

        private async Task<Dictionary<string, List<List<string>>>> HandleGetSheetDataAsync(string fileId, Dictionary<string, string> messages)
        {
            Dictionary<string, List<List<string>>> result = [];

            // 1. 获取文件所有工作表信息
            SheetInfoResponse? sheetInfo = await GetSheetInfoAsync(fileId);
            if (sheetInfo == null || sheetInfo.Code != 0 || sheetInfo.Properties == null)
            {
                return result;
            }

            SaveFileDataToLocalFile(fileId, sheetInfo);

            // 2. 循环读取工作表数据
            Dictionary<string, SheetRangeDataResponse> sheetList = [];
            foreach (SheetProperty sheet in sheetInfo.Properties)
            {
                string? sheetTitle = sheet.Title;
                string? sheetId = sheet.SheetId;
                if (string.IsNullOrEmpty(sheetTitle) || string.IsNullOrEmpty(sheetId)) continue;

                // 3. 确定范围：从 A1 到最大行列
                int maxRow = sheet.RowCount ?? 1000;
                int maxCol = sheet.ColumnCount ?? 26;
                if (maxRow == 0) maxRow = 1000;
                if (maxCol == 0) maxCol = 26;
                string maxColLetter = ColumnNumberToLetter(maxCol);
                string range = $"A1:{maxColLetter}{maxRow}";

                // 4. 调用获取范围数据接口
                SheetRangeDataResponse? sheetData = await GetSheetRangeDataAsync(fileId, sheetId, range);
                if (sheetData != null)
                {
                    sheetList[sheetTitle] = sheetData;
                }
            }
            result = FormatDataAndSave(fileId, sheetList, messages);

            return result;
        }

        public async Task<SheetInfoResponse?> GetSheetInfoAsync(string fileId)
        {
            string requestUrl = $"https://docs.qq.com/openapi/spreadsheet/v3/files/{fileId}?concise=0";
            HttpRequestMessage request = new(HttpMethod.Get, requestUrl);
            AddHeadersToRequest(request, config.ClientID, config.OpenId, config.AccessToken);

            using HttpClient httpClient = httpClientFactory.CreateClient();
            HttpResponseMessage response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<SheetInfoResponse>();
        }

        public async Task<SheetRangeDataResponse?> GetSheetRangeDataAsync(string fileId, string sheetId, string range)
        {
            string requestUrl = $"https://docs.qq.com/openapi/spreadsheet/v3/files/{fileId}/{sheetId}/{range}";
            HttpRequestMessage request = new(HttpMethod.Get, requestUrl);
            AddHeadersToRequest(request, config.ClientID, config.OpenId, config.AccessToken);

            using HttpClient httpClient = httpClientFactory.CreateClient();
            HttpResponseMessage response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<SheetRangeDataResponse>();
        }

        private Dictionary<string, List<List<string>>> FormatDataAndSave(string fileId, Dictionary<string, SheetRangeDataResponse> data, Dictionary<string, string> messages, string title = "")
        {
            Dictionary<string, List<List<string>>> result = [];

            foreach (string sheetTitle in data.Keys)
            {
                SheetRangeDataResponse sheetRangeData = data[sheetTitle];
                if (sheetRangeData.Code != 0)
                {
                    messages[sheetTitle] = $"API 返回错误：{sheetRangeData.Message} (Code: {sheetRangeData.Code})";
                    continue;
                }

                // 将数据转换为易读的二维数组
                if (sheetRangeData.GridData?.Rows != null)
                {
                    List<List<string>> table = [];
                    foreach (RowData row in sheetRangeData.GridData.Rows)
                    {
                        List<string> rowData = [];
                        if (row.Values != null)
                        {
                            foreach (CellData cell in row.Values)
                            {
                                rowData.Add(GetCellDisplayValue(cell));
                            }
                        }
                        table.Add(rowData);
                    }
                    result[sheetTitle] = table;
                }
            }
            memoryCache.Set($"sheet::{fileId}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), TimeSpan.FromHours(2));
            SaveSheetDataToLocalFile(fileId, result);

            if (title != "" && result.TryGetValue(title, out List<List<string>>? value) && value != null)
            {
                result = new()
                {
                    [title] = value
                };
            }

            return result;
        }

        private static string GetCellDisplayValue(CellData cell)
        {
            if (cell?.CellValue == null) return "";

            // 优先根据 dataType 判断数据类型，然后提取对应的字段
            string dataType = cell.DataType ?? "";
            CellValue cellValue = cell.CellValue;

            // 文本类型
            if (!string.IsNullOrEmpty(cellValue.Text))
            {
                return cellValue.Text;
            }

            // 数值类型
            if (cellValue.Number.HasValue)
            {
                return cellValue.Number.Value.ToString();
            }

            // 超链接类型：优先返回展示文本，其次返回 URL
            if (cellValue.Link != null)
            {
                return !string.IsNullOrEmpty(cellValue.Link.Text) ? cellValue.Link.Text : cellValue.Link.Url ?? "";
            }

            // 时间类型
            if (cellValue.Time != null)
            {
                Time t = cellValue.Time;
                return $"{t.Year}-{t.Month:D2}-{t.Day:D2} {t.Hour:D2}:{t.Minute:D2}:{t.Second:D2}";
            }

            // 地理位置类型
            if (cellValue.Location != null)
            {
                return cellValue.Location.Name ?? $"({cellValue.Location.Latitude}, {cellValue.Location.Longitude})";
            }

            // 单选/多选类型：返回选中的选项文本，用逗号连接
            if (cellValue.Select != null && cellValue.Select.Value != null)
            {
                // 如果 options 中有对应的 id，可以获取选项文本
                if (cellValue.Select.Options != null && cellValue.Select.Value.Length > 0)
                {
                    List<string>? selectedTexts = [.. cellValue.Select.Value.Select(id => cellValue.Select.Options.FirstOrDefault(o => o.Id == id)?.Text ?? id)];
                    return string.Join(", ", selectedTexts);
                }
                return string.Join(", ", cellValue.Select.Value);
            }

            // 兼容旧代码：如果以上都没有，尝试返回 text 字段（可能为空字符串）
            return cellValue.Text ?? "";
        }

        private static void AddHeadersToRequest(HttpRequestMessage request, string clientId, string openId, string accessToken)
        {
            request.Headers.Add("Access-Token", accessToken);
            request.Headers.Add("Client-Id", clientId);
            request.Headers.Add("Open-Id", openId);
        }

        private void SaveFileDataToLocalFile(string fileId, SheetInfoResponse data)
        {
            try
            {
                _lockSaveFileData.TryEnter(TimeSpan.FromSeconds(10));
                PluginConfig fileConfig = new("files", fileId);
                fileConfig.LoadConfig();
                fileConfig["data"] = data;
                fileConfig.SaveConfig();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error: {ex.Message}", ex.Message);
            }
            finally
            {
                _lockSaveFileData.Exit();
            }
        }

        private void SaveSheetDataToLocalFile(string fileId, Dictionary<string, List<List<string>>> data)
        {
            try
            {
                _lockSaveSheetData.TryEnter(TimeSpan.FromSeconds(10));
                PluginConfig tableConfig = new("sheets", fileId);
                tableConfig.LoadConfig();
                foreach (string title in data.Keys)
                {
                    tableConfig[title] = data[title];
                }
                tableConfig.SaveConfig();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error: {ex.Message}", ex.Message);
            }
            finally
            {
                _lockSaveSheetData.Exit();
            }
        }

        private static string ColumnNumberToLetter(int col)
        {
            string result = "";
            while (col > 0)
            {
                col--;
                result = (char)('A' + col % 26) + result;
                col /= 26;
            }
            return result;
        }
    }
}
