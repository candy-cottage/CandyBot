using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CandyCottage.CandyBot.Utilities;

namespace CandyCottage.CandyBot.Services;

public sealed class DeepSeekSheetParseDiagnostic(
    ILogger<DeepSeekSheetParseDiagnostic> logger,
    IHttpClientFactory httpClientFactory,
    DeepSeekConfig config) : ISheetParseDiagnostic
{
    public async Task<string> DiagnoseAsync(string sheetName, IReadOnlyList<IReadOnlyList<string>> sampleRows, CancellationToken cancellationToken = default)
    {
        if (!config.Enabled || string.IsNullOrWhiteSpace(config.ApiKey)) return "";

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));

        object body = new
        {
            model = config.Model,
            messages = new object[]
            {
                new { role = "system", content = "你只做只读诊断。用户会给一个腾讯文档工作表样本；它本应是 Forza 车辆调校表。请用一句中文说明为什么未识别到制造商/型号主表头，不要输出修复代码。" },
                new { role = "user", content = $"工作表：{sheetName}\n样本行 JSON：{JsonSerializer.Serialize(sampleRows)}" },
            },
            temperature = 0.2,
            max_tokens = 120,
        };

        using HttpRequestMessage request = new(HttpMethod.Post, $"{config.BaseUrl.TrimEnd('/')}/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);

        try
        {
            using HttpClient client = httpClientFactory.CreateClient();
            using HttpResponseMessage response = await client.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode) return "";

            using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            return doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString()?.Trim() ?? "";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "DeepSeek 表格诊断失败");
            return "";
        }
    }
}
