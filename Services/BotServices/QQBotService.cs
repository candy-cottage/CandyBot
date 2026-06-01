using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Utilities;
using Microsoft.Extensions.Caching.Memory;

namespace CandyCottage.CandyBot.Services.BotServices
{
    public class QQBotService(BotConfig botConfig, ILogger<QQBotService> logger, IHttpClientFactory httpClientFactory, IMemoryCache memoryCache) : IBotReplySender
    {
        private BotConfig BotConfig { get; set; } = botConfig;
        private ILogger<QQBotService> Logger { get; set; } = logger;
        private HttpClient HttpClient { get; set; } = httpClientFactory.CreateClient();
        private IMemoryCache MemoryCache { get; set; } = memoryCache;

        private const string AccessTokenCacheKey = "QQBotAccessToken";

        public async Task<string> GetAccessTokenAsync(bool useCache = true)
        {
            if (useCache && MemoryCache.TryGetValue(AccessTokenCacheKey, out string? accessToken) && !string.IsNullOrEmpty(accessToken))
            {
                return accessToken;
            }

            return await RefreshTokenAsync();
        }

        public async Task<string> RefreshTokenAsync()
        {
            HttpRequestMessage request = new(HttpMethod.Post, "https://bots.qq.com/app/getAppAccessToken")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { appId = BotConfig.AppId, clientSecret = BotConfig.Secret }), Encoding.UTF8, "application/json")
            };
            HttpResponseMessage response = await HttpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            string responseBody = await response.Content.ReadAsStringAsync();
            AccessTokenResponse? tokenResponse = JsonSerializer.Deserialize<AccessTokenResponse>(responseBody);
            if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken) || !int.TryParse(tokenResponse.ExpiresIn, out int expiresIn))
            {
                throw new Exception("获取 Access Token 失败！");
            }
            MemoryCache.Set(AccessTokenCacheKey, tokenResponse.AccessToken, TimeSpan.FromSeconds(expiresIn - 60));
            if (Logger.IsEnabled(LogLevel.Debug)) Logger.LogDebug("获取到 Access Token：{tokenResponse.AccessToken}", tokenResponse.AccessToken);
            return tokenResponse.AccessToken;
        }

        public async Task<UploadMediaResult> UploadC2CMediaAsync(string openid, int fileType, string url)
        {
            return await UploadMediaAsync($"/v2/users/{openid}/files", fileType, url);
        }

        public async Task<UploadMediaResult> UploadGroupMediaAsync(string groupOpenid, int fileType, string url)
        {
            return await UploadMediaAsync($"/v2/groups/{groupOpenid}/files", fileType, url);
        }

        public async Task SendAsync(IBotMessage msg, BotReply reply, int? msgSeq = null)
        {
            if (reply.Markdown != null && !string.IsNullOrWhiteSpace(reply.Markdown.Content))
            {
                // 发送 Markdown + 键盘
                await SendMarkdownAsync(msg, reply.Markdown, reply.Keyboard, msgSeq);
            }
            else if (reply.Keyboard != null)
            {
                // 发送 文本 + 键盘
                await SendMarkdownAsync(msg, new()
                {
                    Content = reply.Text
                }, reply.Keyboard, msgSeq);
            }
            else if (!string.IsNullOrWhiteSpace(reply.Text))
            {
                // 发送纯文本
                await SendTextAsync(msg, reply.Text, msgSeq: msgSeq);
            }
        }

        public async Task SendTextAsync(IBotMessage msg, string content, int msgType = 0, object? media = null, int? msgSeq = null)
        {
            if (msg.IsGroup)
            {
                content = "\r\n" + content.Trim();
                await SendGroupMessageAsync(msg.OpenId, content, msgType, media, msg.SendProactive ? null : msg.Id, msgSeq);
            }
            else
            {
                content = content.Trim();
                await SendC2CMessageAsync(msg.OpenId, content, msgType, media, msg.SendProactive ? null : msg.Id, msgSeq);
            }
        }

        public async Task SendMarkdownAsync(IBotMessage msg, MarkdownMessage mdMsg, KeyboardMessage? kbMsg = null, int? msgSeq = null)
        {
            if (msg.IsGroup)
            {
                await SendGroupMarkdownAsync(msg.OpenId, mdMsg, kbMsg, msg.SendProactive ? null : msg.Id, msgSeq);
            }
            else
            {
                await SendC2CMarkdownAsync(msg.OpenId, mdMsg, kbMsg, msg.SendProactive ? null : msg.Id, msgSeq);
            }
        }

        private async Task SendC2CMessageAsync(string openid, string content, int msgType = 0, object? media = null, string? msgId = null, int? msgSeq = null)
        {
            await SendMessageAsync($"/v2/users/{openid}/messages", content, msgType, media, msgId, msgSeq);
        }

        private async Task SendGroupMessageAsync(string groupOpenid, string content, int msgType = 0, object? media = null, string? msgId = null, int? msgSeq = null)
        {
            await SendMessageAsync($"/v2/groups/{groupOpenid}/messages", content, msgType, media, msgId, msgSeq);
        }

        private async Task SendC2CMarkdownAsync(string openid, MarkdownMessage markdown, KeyboardMessage? keyboard = null, string? msgId = null, int? msgSeq = null)
        {
            await SendMarkdownMessageAsync($"/v2/users/{openid}/messages", markdown, keyboard, msgId, msgSeq);
        }

        private async Task SendGroupMarkdownAsync(string groupOpenid, MarkdownMessage markdown, KeyboardMessage? keyboard = null, string? msgId = null, int? msgSeq = null)
        {
            await SendMarkdownMessageAsync($"/v2/groups/{groupOpenid}/messages", markdown, keyboard, msgId, msgSeq);
        }

        private async Task SendMessageAsync(string url, string content, int msgType = 0, object? media = null, string? msgId = null, int? msgSeq = null)
        {
            string accessToken = await GetAccessTokenAsync();
            HttpRequestMessage request = new(HttpMethod.Post, $"https://api.sgroup.qq.com{url}");
            request.Headers.Authorization = new AuthenticationHeaderValue("QQBot", accessToken);
            if (Logger.IsEnabled(LogLevel.Debug)) Logger.LogDebug("使用的 Access Token：{accessToken}", accessToken);
            Dictionary<string, object> requestBody = new()
            {
                { "content", content },
                { "msg_type", msgType }
            };
            if (media != null)
            {
                requestBody.Add("media", media);
            }
            if (!string.IsNullOrEmpty(msgId))
            {
                requestBody.Add("msg_id", msgId);
            }
            if (msgSeq.HasValue)
            {
                requestBody.Add("msg_seq", msgSeq.Value);
            }
            request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            HttpResponseMessage response = await HttpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync();
                if (Logger.IsEnabled(LogLevel.Error)) Logger.LogError("状态码：{response.StatusCode}，错误信息：{errorBody}", response.StatusCode, errorBody);
            }
        }

        private async Task SendMarkdownMessageAsync(string url, MarkdownMessage markdown, KeyboardMessage? keyboard = null, string? msgId = null, int? msgSeq = null)
        {
            string accessToken = await GetAccessTokenAsync();
            HttpRequestMessage request = new(HttpMethod.Post, $"https://api.sgroup.qq.com{url}");
            request.Headers.Authorization = new AuthenticationHeaderValue("QQBot", accessToken);
            Dictionary<string, object> body = new()
            {
                { "msg_type", 2 },
                { "markdown", markdown }
            };
            if (keyboard != null) body.Add("keyboard", keyboard);
            if (!string.IsNullOrEmpty(msgId)) body.Add("msg_id", msgId);
            if (msgSeq.HasValue) body.Add("msg_seq", msgSeq.Value);
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            HttpResponseMessage response = await HttpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync();
                if (Logger.IsEnabled(LogLevel.Error)) Logger.LogError("状态码：{response.StatusCode}，错误信息：{errorBody}", response.StatusCode, errorBody);
            }
        }

        private async Task<UploadMediaResult> UploadMediaAsync(string url, int fileType, string fileUrl)
        {
            string accessToken = await GetAccessTokenAsync();
            HttpRequestMessage request = new(HttpMethod.Post, $"https://api.sgroup.qq.com{url}");
            request.Headers.Authorization = new AuthenticationHeaderValue("QQBot", accessToken);

            Dictionary<string, object> requestBody = new()
            {
                { "file_type", fileType },
                { "url", fileUrl },
                { "srv_send_msg", false }
            };
            request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            HttpResponseMessage response = await HttpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync();
                string error = $"状态码：{response.StatusCode}，错误信息：{errorBody}，多媒体URL地址：{fileUrl}";
                if (Logger.IsEnabled(LogLevel.Error)) Logger.LogError("{Error}", error);
                return new UploadMediaResult { Error = error };
            }

            string responseBody = await response.Content.ReadAsStringAsync();
            MediaResponse? mediaResponse = JsonSerializer.Deserialize<MediaResponse>(responseBody);
            if (mediaResponse == null)
            {
                return new UploadMediaResult { Error = "反序列化富媒体消息失败。" };
            }

            if (Logger.IsEnabled(LogLevel.Debug)) Logger.LogDebug("接收到的富媒体消息：{FileInfo}，有效时间：{Ttl}", mediaResponse.FileInfo, mediaResponse.Ttl);

            return new UploadMediaResult
            {
                FileUuid = mediaResponse.FileUuid,
                FileInfo = mediaResponse.FileInfo,
                Ttl = mediaResponse.Ttl
            };
        }
    }
}
