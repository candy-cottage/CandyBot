using CandyCottage.CandyBot.Interfaces;
using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Services.BotServices;
using CandyCottage.CandyBot.Services.SystemServices;
using CandyCottage.CandyBot.Utilities;
using Microsoft.Extensions.Caching.Memory;
using Milimoe.FunGame.Core.Api.Transmittal;
using Milimoe.FunGame.Core.Api.Utility;
using Milimoe.FunGame.Core.Entity;
using ProjectRedbud.FunGame.SQLQueryExtension;

namespace CandyCottage.CandyBot.Services
{
    public partial class CandyBotService(ILogger<CandyBotService> logger, IMemoryCache memoryCache, SQLHelper sql, CandyBotFunctionConfig functionConfig, ForzaConfig forzaConfig,
        IBackgroundTaskQueue taskQueue, ITaskSchedulerManager taskManager,
        IBotReplySender botService, BettingService bettingService, SQLTableInitializeService sqlInitService, UserService userService, ForzaService forzaService,
        IForzaSheetImporter forzaSheetImporter, Esports.EsportsSyncService esportsSyncService, UserAPITokenService userAPITokenService)
    {
        public async Task Handler(IBotMessage e)
        {
            try
            {
                if (e is null)
                {
                    return;
                }

                User? user = null;
                string openid = e.AuthorOpenId;
                string autokey = openid.Encrypt(openid);
                if (openid != "")
                {
                    string cacheKey = $"bot::{openid}";
                    if (memoryCache.TryGetValue(cacheKey, out object? value) && value is User u)
                    {
                        user = u;
                    }
                    else
                    {
                        user = sql.GetUserByUsernameAndAutoKey(openid, autokey);
                        if (user != null)
                        {
                            sql.UpdateLastLogin(openid, "");
                            memoryCache.Set(cacheKey, user, TimeSpan.FromMinutes(10));
                        }
                    }
                }

                // 系统级指令
                if (await HandleSystemInput(e, user))
                {
                    return;
                }

                if (WillGoOn(functionConfig.System, user))
                {
                    // 帮助指令相关
                    if (await HandleShowHelpInput(e, user))
                    {
                        return;
                    }

                    // 糖果屋相关
                    if (await HandleCandyCottageInput(e, user))
                    {
                        return;
                    }

                    // 用户存档相关
                    if (await HandleUserSavedInput(e, user))
                    {
                        return;
                    }

                    // 地平线相关
                    if (await HandleForzaHorizonInput(e, user))
                    {
                        return;
                    }

                    // 赛事预测相关
                    if (await HandleBettingInput(e, user))
                    {
                        return;
                    }
                }

                // 系统功能开关控制
                if (await HandleToggleCommand(e, user))
                {
                    return;
                }

                if (!functionConfig.System) await botService.SendAsync(e, "糖果屋正在维护，请稍后再试。");
                return;
            }
            catch (Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Error)) logger.LogError("Error: {ex}", ex);
            }

            return;
        }

        public async Task HandlerFullGroup(GroupMessage e)
        {
            if (e is null)
            {
                return;
            }

            if (logger.IsEnabled(LogLevel.Information)) logger.LogInformation("接收到群聊全量消息：{Username}说: {Detail}", e.Author.Username, e.Detail);
        }

        private async Task<bool> HandleToggleCommand(IBotMessage e, User? user)
        {
            string detail = e.Detail;
            if (!detail.StartsWith("开启") && !detail.StartsWith("关闭"))
            {
                return false;
            }

            if (!await CheckUser(e, user))
            {
                return true;
            }

            string action = detail[..2];
            string systemName = detail[2..];
            if (!functionConfig.ToggleConfigs.TryGetValue(systemName, out var func))
            {
                return false;
            }

            bool isEnable = action == "开启";
            lock (functionConfig.Lock)
            {
                func.Setter(functionConfig, isEnable);
                functionConfig.Config.SaveConfig();
            }
            await botService.SendAsync(e, $"{func.Description}已{(isEnable ? "开启" : "关闭")}。");
            return true;
        }

        public async Task<bool> CheckUser(IBotMessage e, User? user, bool checkAdmin = true, bool checkOperator = true)
        {
            if (user is null)
            {
                await botService.SendAsync(e, new BotReply()
                {
                    Markdown = new()
                    {
                        Content = "创建存档后可使用相关功能。"
                    },
                    Keyboard = new KeyboardMessage().AppendButtons(2, [
                        Button.CreateCmdButton("⚙️ 创建存档", "创建存档"),
                        Button.CreateCmdButton("🖌 再试一次", e.Detail)
                    ])
                });
                return false;
            }
            if (!user.IsAdmin && (checkAdmin || (checkOperator && !user.IsOperator)))
            {
                await botService.SendAsync(e, "你没有权限执行此操作。");
                return false;
            }
            return true;
        }

        public bool WillGoOn(bool isFunctionOpen, User? user)
        {
            if (isFunctionOpen || (!isFunctionOpen && functionConfig.AllowAdminsDebug && (user?.IsAdmin ?? false)))
            {
                return true;
            }
            return false;
        }

        public async Task TryCatchFinally(System.Action action, Action<Exception> err, System.Action fin)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Error)) logger.LogError("Error: {ex}", ex);
                err(ex);
            }
            finally
            {
                fin();
            }
        }

        public async Task SendUserNotice(long userId, BotReply reply, string title = "系统通知", bool showTime = true)
        {
            await taskQueue.QueueAsync(async (token) =>
            {
                if (sql.GetUserById(userId) is User user)
                {
                    IBotMessage e = new C2CMessage()
                    {
                        Author = new()
                        {
                            Bot = false,
                            Id = user.Username,
                        },
                        Detail = "",
                        SendProactive = true
                    };
                    if (title != "" && reply.Markdown != null)
                    {
                        reply.Markdown.Content = $"**【{title}】{(showTime ? DateTime.Now.ToString(Milimoe.FunGame.Core.Library.Constant.General.GeneralDateTimeFormatChinese) : "")}**\n\n{reply.Markdown.Content}";
                    }
                    await botService.SendAsync(e, reply);
                }
            });
        }

        public async Task AddQueuedHostedTask(Func<Task> task)
        {
            await taskQueue.QueueAsync(async (token) =>
            {
                await task();
            });
        }

        private readonly string _busy = "服务器繁忙，请稍后再试。";
        [System.Text.RegularExpressions.GeneratedRegex(@"\d+")]
        private static partial System.Text.RegularExpressions.Regex GetFirstNumber();
        [System.Text.RegularExpressions.GeneratedRegex(@"(\w+)=(?:""([^""]*)""|(\S+))")]
        private static partial System.Text.RegularExpressions.Regex GetParamValue();
        [System.Text.RegularExpressions.GeneratedRegex(@"^([A-Za-z]\d*)(.*)$")]
        private static partial System.Text.RegularExpressions.Regex MatchClassAndMatchType();
    }
}
