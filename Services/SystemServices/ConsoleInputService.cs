using System.Text;
using CandyCottage.CandyBot.Models;
using Milimoe.FunGame.Core.Api.Utility;

namespace CandyCottage.CandyBot.Services.SystemServices
{
    public class ConsoleInputService(ILogger<ConsoleInputService> logger, IServiceProvider serviceProvider, IHostApplicationLifetime applicationLifetime) : BackgroundService
    {
        private string adminOpenID = "";
        private readonly PluginConfig _initializationConfig = new("sqlconfig", "initialization");

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _initializationConfig.LoadConfig();
            adminOpenID = _initializationConfig.Get<string>("admin") ?? "";
            while (!stoppingToken.IsCancellationRequested)
            {
                string input = (await Console.In.ReadLineAsync(stoppingToken))?.Trim() ?? "";
                if (input != "" && !stoppingToken.IsCancellationRequested)
                {
                    List<string> strings = ParseCommandLine(input);
                    if (strings.Count > 0)
                    {
                        string order = strings[0].ToLower();
                        string[] inputArgs = [.. strings.Skip(1)];
                        switch (order)
                        {
                            case "exit":
                            case "quit":
                                logger.LogInformation("正在退出程序...");
                                applicationLifetime.StopApplication();
                                break;
                            case ".cd":
                                if (logger.IsEnabled(LogLevel.Information)) logger.LogInformation("使用 .cd 指令，参数：{args}", string.Join(", ", inputArgs));
                                if (inputArgs.Length > 0)
                                {
                                    string subOrder = inputArgs[0].ToLower();
                                    switch (subOrder)
                                    {
                                        case "refresh":
                                            _initializationConfig.LoadConfig();
                                            adminOpenID = _initializationConfig.Get<string>("admin") ?? "";
                                            break;
                                        default:
                                            break;
                                    }
                                }
                                break;
                            default:
                                try
                                {
                                    ConsoleInputMessage msg = new()
                                    {
                                        AuthorOpenId = adminOpenID == "" ? Guid.NewGuid().ToString("N") : adminOpenID,
                                        Detail = input
                                    };
                                    using IServiceScope? scope = serviceProvider.CreateScope();
                                    CandyBotService candyBotService = scope.ServiceProvider.GetRequiredService<CandyBotService>();
                                    await candyBotService.Handler(msg);
                                }
                                catch (Exception ex)
                                {
                                    logger.LogError(ex, "执行控制台命令时发生错误：{ex.Message}", ex.Message);
                                }
                                break;
                        }
                    }
                }
            }
        }

        private static List<string> ParseCommandLine(string input)
        {
            ReadOnlySpan<char> inputSpan = input.AsSpan();
            List<string> strings = [];
            StringBuilder current = new();
            bool inQuotes = false;

            for (int i = 0; i < inputSpan.Length; i++)
            {
                char c = inputSpan[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        // 检查是否是转义引号 \"
                        if (i + 1 < inputSpan.Length && inputSpan[i + 1] == '"')
                        {
                            current.Append('"');
                            // 跳过下一个引号
                            i++;
                        }
                        else
                        {
                            // 结束引号
                            inQuotes = false;
                        }
                    }
                    else if (c == '\\' && i + 1 < inputSpan.Length && inputSpan[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        current.Append(c);
                    }
                }
                else
                {
                    if (c == '"')
                    {
                        inQuotes = true;
                    }
                    else if (char.IsWhiteSpace(c))
                    {
                        if (current.Length > 0)
                        {
                            strings.Add(current.ToString());
                            current.Clear();
                        }
                    }
                    else
                    {
                        current.Append(c);
                    }
                }
            }

            // 收尾最后一个参数
            if (current.Length > 0)
            {
                strings.Add(current.ToString());
            }

            return strings;
        }
    }
}
