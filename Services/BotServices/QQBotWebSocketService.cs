using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Utilities;

namespace CandyCottage.CandyBot.Services.BotServices
{
    public class QQBotWebSocketService(ILogger<QQBotWebSocketService> logger, IHttpClientFactory httpClientFactory, BotConfig botConfig, IServiceProvider serviceProvider) : BackgroundService
    {
        private readonly ILogger<QQBotWebSocketService> _logger = logger;
        private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
        private readonly IServiceProvider _serviceProvider = serviceProvider;
        private readonly BotConfig _botConfig = botConfig;

        private ClientWebSocket? _webSocket;
        private CancellationTokenSource? _cts;
        private string? _sessionId;
        private int _lastSeq;
        private int _heartbeatIntervalMs;
        private Timer? _heartbeatTimer;
        /// <summary>
        /// 是否收到上次心跳的ACK
        /// </summary>
        private bool _isHeartbeatAckReceived = true;
        /// <summary>
        /// 连续未收到ACK的次数
        /// </summary>
        private int _missedHeartbeatCount = 0;
        /// <summary>
        /// 最大容忍连续无ACK次数
        /// </summary>
        private const int MaxMissedHeartbeats = 3;
        private readonly Lock _heartbeatLock = new();

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // 主重连循环
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ConnectAndRunAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "WebSocket 连接异常崩溃，5秒后重连...");
                    await Task.Delay(5000, stoppingToken);
                }
            }
        }

        private async Task ConnectAndRunAsync(CancellationToken stoppingToken)
        {
            StopHeartbeatTimer();

            // 1. 获取网关地址
            string gatewayUrl = await GetGatewayUrlAsync(stoppingToken);
            if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("获取网关地址: {GatewayUrl}", gatewayUrl);

            // 2. 建立连接
            _webSocket = new ClientWebSocket();
            await _webSocket.ConnectAsync(new Uri(gatewayUrl), stoppingToken);
            if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("WebSocket 已连接");

            _cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

            // 3. 接收消息循环
            byte[] buffer = new byte[4096];
            StringBuilder messageBuilder = new();
            try
            {
                while (!stoppingToken.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            _logger.LogWarning("服务端主动关闭连接，准备重连");
                            await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                            return; // 退出当前运行循环，外层会重连
                        }
                        messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    }
                    while (!result.EndOfMessage);

                    string message = messageBuilder.ToString();
                    messageBuilder.Clear();

                    await ProcessMessageAsync(message, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Error)) _logger.LogError(ex, "WebSocket 接收消息异常");
            }
            finally
            {
                // 确保连接退出时停止心跳计时器
                StopHeartbeatTimer();
            }
        }

        private async Task ProcessMessageAsync(string message, CancellationToken stoppingToken)
        {
            try
            {
                Payload? payload = JsonSerializer.Deserialize<Payload>(message);
                if (payload == null) return;

                if (payload.SequenceNumber > 0)
                    _lastSeq = payload.SequenceNumber;

                switch (payload.Op)
                {
                    case 10: // Hello
                        {
                            WebSocketHelloData? hello = JsonSerializer.Deserialize<WebSocketHelloData>(payload.Data.ToString() ?? "");
                            if (hello != null)
                            {
                                _heartbeatIntervalMs = hello.HeartbeatInterval;
                                if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("收到 Hello，心跳间隔: {Interval}ms", _heartbeatIntervalMs);
                                StartHeartbeatTimer();
                                // 尝试恢复会话或重新鉴权
                                if (!string.IsNullOrEmpty(_sessionId))
                                    await SendResumeAsync(stoppingToken);
                                else
                                    await SendIdentifyAsync(stoppingToken);
                            }
                            break;
                        }

                    case 0: // Dispatch
                        {
                            await HandleDispatchAsync(payload);
                            break;
                        }

                    case 11: // Heartbeat ACK
                        {
                            lock (_heartbeatLock)
                            {
                                _isHeartbeatAckReceived = true;
                                _missedHeartbeatCount = 0;
                            }
                            _logger.LogTrace("收到心跳 ACK");
                            break;
                        }

                    case 7: // Reconnect
                        {
                            _logger.LogWarning("收到 Op 7 (Reconnect)，主动重连");
                            await ReconnectAsync();
                            break;
                        }

                    case 9: // Invalid Session
                        {
                            string errorDetail = "未知";
                            if (payload.Data is JsonElement elem)
                            {
                                switch (elem.ValueKind)
                                {
                                    case JsonValueKind.True:
                                        errorDetail = "true";
                                        break;
                                    case JsonValueKind.False:
                                        errorDetail = "false";
                                        break;
                                    case JsonValueKind.Number:
                                        if (elem.TryGetInt32(out int code))
                                            errorDetail = code.ToString();
                                        else
                                            errorDetail = elem.ToString();
                                        break;
                                    default:
                                        errorDetail = elem.ToString();
                                        break;
                                }
                            }
                            else if (payload.Data != null)
                            {
                                errorDetail = payload.Data.ToString() ?? "null";
                            }

                            _logger.LogWarning("收到 Op 9 (Invalid Session)，错误详情: {ErrorDetail}。服务器将关闭连接。", errorDetail);
                            _sessionId = null;
                            _lastSeq = 0;
                            break;
                        }

                    default:
                        if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("收到未处理的 Op: {Op}", payload.Op);
                        break;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "解析消息失败: {Msg}", message);
            }
        }

        private void StartHeartbeatTimer()
        {
            StopHeartbeatTimer();
            _heartbeatTimer = new Timer(async _ =>
            {
                lock (_heartbeatLock)
                {
                    if (!_isHeartbeatAckReceived)
                    {
                        _missedHeartbeatCount++;
                        if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("心跳超时，未收到 ACK 次数: {Count}", _missedHeartbeatCount);
                        if (_missedHeartbeatCount >= MaxMissedHeartbeats)
                        {
                            _logger.LogError("连续 {Max} 次心跳无响应，主动重连", MaxMissedHeartbeats);
                            _ = ReconnectAsync();
                            return;
                        }
                    }
                    _isHeartbeatAckReceived = false;
                }
                await SendHeartbeatAsync();
            }, null, 0, _heartbeatIntervalMs);
        }

        private void StopHeartbeatTimer()
        {
            lock (_heartbeatLock)
            {
                _heartbeatTimer?.Dispose();
                _heartbeatTimer = null;
                // 重置心跳状态
                _isHeartbeatAckReceived = true;
                _missedHeartbeatCount = 0;
            }
        }

        private async Task SendHeartbeatAsync()
        {
            if (_webSocket?.State != WebSocketState.Open) return;
            Payload payload = new() { Op = 1, Data = _lastSeq };
            string json = JsonSerializer.Serialize(payload);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            try
            {
                await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "发送心跳失败");
            }
        }

        private async Task SendIdentifyAsync(CancellationToken ct)
        {
            if (_webSocket?.State != WebSocketState.Open) return;
            string accessToken = await GetAccessTokenAsync();
            string authorization = $"QQBot {accessToken}";

            WebSocketIdentifyData identify = new()
            {
                Token = authorization,
                Intents = (1L << 30) | (1L << 25) | (1L << 12),
                Properties = new Dictionary<string, string>
                {
                    ["$os"] = "linux",
                    ["$browser"] = "qqbot_csharp",
                    ["$device"] = "qqbot_csharp"
                }
            };
            Payload payload = new() { Op = 2, Data = identify };
            string json = JsonSerializer.Serialize(payload);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
            if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("已发送 Identify 鉴权");
        }

        private async Task SendResumeAsync(CancellationToken ct)
        {
            if (_webSocket?.State != WebSocketState.Open) return;
            string accessToken = await GetAccessTokenAsync();
            string authorization = $"QQBot {accessToken}";

            WebSocketResumeData resume = new()
            {
                Token = authorization,
                SessionId = _sessionId ?? "",
                Seq = _lastSeq
            };
            Payload payload = new() { Op = 6, Data = resume };
            string json = JsonSerializer.Serialize(payload);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
            if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("已发送 Resume，session={SessionId}, seq={Seq}", _sessionId, _lastSeq);
        }

        private async Task ReconnectAsync()
        {
            _logger.LogWarning("执行主动重连");
            StopHeartbeatTimer();

            try
            {
                if (_webSocket?.State == WebSocketState.Open)
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Reconnecting", CancellationToken.None);
                _webSocket?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "关闭 WebSocket 时出错");
            }
            finally
            {
                _webSocket = null;
            }

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            // 外层循环会自动重连
        }

        private async Task<string> GetGatewayUrlAsync(CancellationToken ct)
        {
            using HttpClient? httpClient = _httpClientFactory.CreateClient();
            string accessToken = await GetAccessTokenAsync(false);
            string authorization = $"QQBot {accessToken}";
            httpClient.DefaultRequestHeaders.Add("Authorization", authorization);
            HttpResponseMessage resp = await httpClient.GetAsync($"{_botConfig.GatewayHost}/gateway", ct);
            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync(ct);
            JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("url").GetString() ?? throw new Exception("无法获取网关地址");
        }

        private async Task<string> GetAccessTokenAsync(bool useCache = true)
        {
            using IServiceScope scope = _serviceProvider.CreateScope();
            QQBotService qqBotService = scope.ServiceProvider.GetRequiredService<QQBotService>();
            return await qqBotService.GetAccessTokenAsync(useCache);
        }

        private async Task HandleDispatchAsync(Payload payload)
        {
            using IServiceScope? scope = _serviceProvider.CreateScope();

            switch (payload.EventType)
            {
                case "READY":
                    // READY 事件中提取 session_id
                    if (payload.Data is JsonElement elem && elem.ValueKind == JsonValueKind.Object && elem.TryGetProperty("session_id", out JsonElement sid))
                    {
                        _sessionId = sid.GetString();
                        if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("获取 Session ID: {SessionId}", _sessionId);
                    }
                    else
                    {
                        _logger.LogWarning("READY 事件中未找到 session_id，Data: {Data}", payload.Data);
                    }
                    break;
                case "RESUMED":
                    // 会话恢复
                    if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("会话恢复成功 (RESUMED)");
                    break;
                case "C2C_MESSAGE_CREATE":
                    C2CMessage? c2c = JsonSerializer.Deserialize<C2CMessage>(payload.Data.ToString() ?? "");
                    if (c2c != null)
                    {
                        try
                        {
                            c2c.Detail = c2c.Detail.Trim().TrimStart('/');
                            CandyBotService candyBotService = scope.ServiceProvider.GetRequiredService<CandyBotService>();
                            await candyBotService.Handler(c2c);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "处理 C2C_MESSAGE_CREATE 事件时发生异常：{ex.Message}", ex.Message);
                        }
                    }
                    break;
                case "GROUP_MESSAGE_CREATE":
                    GroupMessage? fullGroup = JsonSerializer.Deserialize<GroupMessage>(payload.Data.ToString() ?? "");
                    if (fullGroup != null)
                    {
                        try
                        {
                            CandyBotService candyBotService = scope.ServiceProvider.GetRequiredService<CandyBotService>();
                            string checkAtBot = $"\u003c@{_botConfig.OpenId}\u003e";
                            if (fullGroup.Content.Contains(checkAtBot))
                            {
                                fullGroup.Detail = fullGroup.Detail.Replace(checkAtBot, "").Trim().TrimStart('/');
                                await candyBotService.Handler(fullGroup);
                            }
                            else
                            {
                                await candyBotService.HandlerFullGroup(fullGroup);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "处理 GROUP_MESSAGE_CREATE 事件时发生异常：{ex.Message}", ex.Message);
                        }
                    }
                    break;
                case "GROUP_AT_MESSAGE_CREATE":
                    GroupMessage? group = JsonSerializer.Deserialize<GroupMessage>(payload.Data.ToString() ?? "");
                    if (group != null)
                    {
                        try
                        {
                            group.Detail = group.Detail.Trim().TrimStart('/');
                            CandyBotService candyBotService = scope.ServiceProvider.GetRequiredService<CandyBotService>();
                            await candyBotService.Handler(group);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "处理 GROUP_AT_MESSAGE_CREATE 事件时发生异常：{ex.Message}", ex.Message);
                        }
                    }
                    break;
                default:
                    if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("未处理事件: {Event}", payload.EventType);
                    break;
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _heartbeatTimer?.Dispose();
            _cts?.Cancel();
            if (_webSocket?.State == WebSocketState.Open)
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Service stopping", cancellationToken);
            await base.StopAsync(cancellationToken);
        }
    }
}
