using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Services.Esports;
using CandyCottage.CandyBot.Utilities;
using Microsoft.Extensions.Caching.Memory;
using Milimoe.FunGame.Core.Entity;
using Milimoe.FunGame.Core.Library.Constant;
using ProjectRedbud.FunGame.SQLQueryExtension;

namespace CandyCottage.CandyBot.Services;

public partial class CandyBotService
{
    private static string[] CheckDateTimeFormat { get; } = ["yyyy-MM-dd HH:mm", "yyyy-MM-dd"];
    private const int EsportsScopeSearchCacheMinutes = 30;

    public async Task<bool> HandleBettingInput(IBotMessage e, User? user)
    {
        long uid = user?.Id ?? 0;
        MarkdownMessage md = new() { Content = _busy };
        BotReply reply = new() { Markdown = md };

        if (!WillGoOn(functionConfig.Betting, user))
        {
            md.Content = "赛事系统正在维护，请稍后再试。";
            await botService.SendAsync(e, reply);
            return true;
        }

        if (e.Detail == "预测帮助" || e.Detail == "赛事帮助")
        {
            md.Content = "🎮 赛事系统帮助：\r\n"
                + $"✨ {"赛事列表".CreateCmdInput()}   - 查看所有赛事\r\n"
                + $"✨ {"比赛列表".CreateCmdInput()}   - 查看所有比赛\r\n"
                + (!functionConfig.Betting_ReadOnly ? $"✨ {"创建存档".CreateCmdInput()}   - 创建存档后可预测\r\n"
                + $"✨ {"我的预测".CreateCmdInput()}   - 查看我的预测记录\r\n"
                + $"✨ {"预测领奖".CreateCmdInput()}   - 领取预测奖励\r\n" : "")
                + $"✨ {"赛事详情".CreateCmdInput()}   - 查看赛事详情\r\n"
                + $"✨ {"比赛详情".CreateCmdInput()}   - 查看单场比赛";
            List<Button> buttons = [
                Button.CreateCmdButton("📋 赛事列表", "赛事列表"),
                Button.CreateCmdButton("📅 比赛列表", "比赛列表")
            ];
            if (!functionConfig.Betting_ReadOnly)
            {
                buttons.AddRange([
                    Button.CreateCmdButton("⚙️ 创建存档", "创建存档"),
                    Button.CreateCmdButton("📜 我的预测", "我的预测"),
                    Button.CreateCmdButton("💰 预测领奖", "预测领奖"),
                    Button.CreateCmdButton("❓ 预测帮助", "预测帮助")
                ]);
            }
            reply.Keyboard = new KeyboardMessage().AppendButtons(2, buttons);
            await botService.SendAsync(e, reply);
            return true;
        }

        if (await HandleBettingScopeFilterInput(e, user))
        {
            return true;
        }

        // 立即同步赛事：管理员手动触发一次赛程抓取 + 结果预填（受赛事自动化开关控制）
        if (e.Detail == "立即同步赛事")
        {
            if (!await CheckUser(e, user, false)) return true;
            if (!WillGoOn(functionConfig.BettingAutomation, user))
            {
                await botService.SendAsync(e, "赛事自动化正在维护，请稍后再试。");
                return true;
            }

            try
            {
                EsportsScheduleSyncReport schedules = await esportsSyncService.SyncSchedulesAsync();
                EsportsResultSyncReport results = await esportsSyncService.SyncResultsAsync();
                md.Content = $"同步完成：抓取 {schedules.Fetched} 场，新增草稿 {schedules.MatchesCreated} 场，更新 {schedules.MatchesUpdated} 场；结果检查 {results.Checked} 场，待确认 {results.PendingSettlementCreated} 场。";
                reply.Keyboard = new KeyboardMessage().AppendButtons(2,
                    Button.CreateCmdButton("📋 待发布列表", "待发布比赛"),
                    Button.CreateCmdButton("✅ 待确认结算", "待确认结算"),
                    Button.CreateCmdButton("📅 比赛列表", "比赛列表"));
            }
            catch (Exception ex)
            {
                // 数据源异常（网络 / 5xx / JSON 解析等）不得静默——保证管理员总能收到反馈。
                if (logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Error)) logger.LogError(ex, "立即同步赛事失败");
                md.Content = $"赛事同步失败：{ex.Message}";
            }
            await botService.SendAsync(e, reply);
            return true;
        }

        // 待发布比赛：管理员查看草稿（未发布）比赛列表
        if (e.Detail.StartsWith("待发布比赛"))
        {
            if (!await CheckUser(e, user, false)) return true;

            int page = 1;
            string detail = e.Detail.Replace("待发布比赛", "").Trim();
            System.Text.RegularExpressions.Match match = GetFirstNumber().Match(detail);
            if (match.Success && int.TryParse(match.Value, out int p)) page = p;

            var (content, totalPages) = bettingService.GetPendingPublishMatches(page);
            md.Content = content;
            KeyboardMessage kb = new();
            if (totalPages > 1) kb.AddPaginationRow("待发布比赛 ", page, totalPages);
            kb.AppendButtonsWithNewRow(2,
                Button.CreateCmdButton("发布比赛", "发布比赛 ", enter: false),
                Button.CreateCmdButton("立即同步赛事", "立即同步赛事"),
                Button.CreateCmdButton("待确认结算", "待确认结算"));
            reply.Keyboard = kb;
            await botService.SendAsync(e, reply);
            return true;
        }

        // 待确认结算：管理员查看自动预填结果、等待二次确认的比赛列表
        if (e.Detail.StartsWith("待确认结算"))
        {
            if (!await CheckUser(e, user, false)) return true;

            int page = 1;
            string detail = e.Detail.Replace("待确认结算", "").Trim();
            System.Text.RegularExpressions.Match match = GetFirstNumber().Match(detail);
            if (match.Success && int.TryParse(match.Value, out int p)) page = p;

            var (content, totalPages) = bettingService.GetPendingSettlementMatches(page);
            md.Content = content;
            KeyboardMessage kb = new();
            if (totalPages > 1) kb.AddPaginationRow("待确认结算 ", page, totalPages);
            kb.AppendButtonsWithNewRow(2,
                Button.CreateCmdButton("确认结算", "确认结算 ", enter: false),
                Button.CreateCmdButton("立即同步赛事", "立即同步赛事"));
            reply.Keyboard = kb;
            await botService.SendAsync(e, reply);
            return true;
        }

        // 发布比赛：管理员将草稿比赛连同父赛事一并发布
        if (e.Detail.StartsWith("发布比赛"))
        {
            if (!await CheckUser(e, user, false)) return true;

            string detail = e.Detail.Replace("发布比赛", "").Trim();
            if (!int.TryParse(detail, out int matchId))
            {
                await botService.SendAsync(e, "格式：发布比赛 <比赛ID>");
                return true;
            }

            md.Content = bettingService.PublishMatch(matchId, out string error)
                ? $"比赛 {matchId} 已发布。"
                : error;
            reply.Keyboard = new KeyboardMessage().AppendButtons(2,
                Button.CreateCmdButton("🔍 比赛详情", $"比赛详情 {matchId}"),
                Button.CreateCmdButton("📋 待发布比赛", "待发布比赛"));
            await botService.SendAsync(e, reply);
            return true;
        }

        // 赛程：显示所有比赛
        if (e.Detail.StartsWith("赛程") || e.Detail.StartsWith("比赛列表"))
        {
            int page = 1;
            string detail = e.Detail.Replace("赛程", "").Replace("比赛列表", "").Trim();
            System.Text.RegularExpressions.Match match = GetFirstNumber().Match(detail);
            if (match.Success && int.TryParse(match.Value, out int p)) page = p;

            var (content, totalPages) = bettingService.GetAllMatches(page);
            md.Content = content;
            KeyboardMessage kb = new();
            if (totalPages > 1)
                kb.AddPaginationRow("赛程 ", page, totalPages);
            List<Button> buttons = [
                Button.CreateCmdButton("📋 赛事列表", "赛事列表"),
                Button.CreateCmdButton("📅 比赛列表", "比赛列表")
            ];
            if (!functionConfig.Betting_ReadOnly)
            {
                buttons.AddRange([
                    Button.CreateCmdButton("📜 我的预测", "我的预测"),
                    Button.CreateCmdButton("❓ 预测帮助", "预测帮助")
                ]);
            }
            kb.AppendButtonsWithNewRow(2, buttons);
            reply.Keyboard = kb;
            await botService.SendAsync(e, reply);
            return true;
        }

        if (e.Detail.StartsWith("比赛详情"))
        {
            string detail = e.Detail.Replace("比赛详情", "").Trim();
            if (int.TryParse(detail, out int matchId))
            {
                string matchDetail = bettingService.GetMatchDetail(matchId, out KeyboardMessage kbFromService);
                md.Content = matchDetail;
                // 获取用户本场预测
                if (!functionConfig.Betting_ReadOnly)
                {
                    var (myBetsContent, _) = bettingService.GetMyBets(uid, matchId, 1, int.MaxValue);
                    if (!string.IsNullOrEmpty(myBetsContent) && myBetsContent != "你还没有任何预测记录。")
                        md.Content += "\r\n" + myBetsContent;
                }
                // 添加额外按钮
                kbFromService ??= new KeyboardMessage();
                List<Button> buttons = [
                    Button.CreateCmdButton("📋 赛事列表", "赛事列表"),
                    Button.CreateCmdButton("📅 比赛列表", "比赛列表")
                ];
                if (!functionConfig.Betting_ReadOnly)
                {
                    buttons.AddRange([
                        Button.CreateCmdButton("💰 预测领奖", "预测领奖"),
                        Button.CreateCmdButton("❓ 预测帮助", "预测帮助")
                    ]);
                }
                kbFromService.AppendButtonsWithNewRow(2, buttons);
                reply.Keyboard = kbFromService;
                await botService.SendAsync(e, reply);
            }
            else
            {
                await botService.SendAsync(e, "格式：比赛详情 <比赛ID>");
            }
            return true;
        }

        // 赛事详情：用于查看某一赛事下的所有比赛
        if (e.Detail.StartsWith("赛事详情"))
        {
            string detail = e.Detail["赛事详情".Length..].Trim();
            // 解析事件ID和页码
            string[] parts = detail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 1 && int.TryParse(parts[0], out int eventId))
            {
                int page = 1;
                if (parts.Length > 1 && int.TryParse(parts[1], out int p)) page = p;

                var (content, totalPages) = bettingService.GetEventDetail(eventId, page);
                md.Content = content;
                KeyboardMessage kb = new();
                if (totalPages > 1)
                    kb.AddPaginationRow($"赛事详情 {eventId} ", page, totalPages);
                List<Button> buttons = [
                    Button.CreateCmdButton("📋 赛事列表", "赛事列表"),
                    Button.CreateCmdButton("📅 比赛列表", "比赛列表")
                ];
                if (!functionConfig.Betting_ReadOnly)
                {
                    buttons.AddRange([
                        Button.CreateCmdButton("📜 我的预测", "我的预测"),
                        Button.CreateCmdButton("💰 预测领奖", "预测领奖")
                    ]);
                }
                kb.AppendButtonsWithNewRow(2, buttons);
                reply.Keyboard = kb;

                await botService.SendAsync(e, reply);
            }
            else
            {
                md.Content = "格式：赛事详情 <赛事ID>";
                List<Button> buttons = [
                    Button.CreateCmdButton("📋 赛事列表", "赛事列表"),
                    Button.CreateCmdButton("📅 比赛列表", "比赛列表")
                ];
                if (!functionConfig.Betting_ReadOnly)
                {
                    buttons.AddRange([
                        Button.CreateCmdButton("❓ 预测帮助", "预测帮助"),
                        Button.CreateCmdButton("📜 我的预测", "我的预测")
                    ]);
                }
                reply.Keyboard = new KeyboardMessage().AppendButtons(2, buttons);
                await botService.SendAsync(e, reply);
            }
            return true;
        }

        // 赛事列表
        if (e.Detail.StartsWith("赛事") || e.Detail.StartsWith("赛事列表"))
        {
            int page = 1;
            string detail = e.Detail.Replace("赛事", "").Replace("赛事列表", "").Trim();
            System.Text.RegularExpressions.Match match = GetFirstNumber().Match(detail);
            if (match.Success && int.TryParse(match.Value, out int p)) page = p;

            var (content, totalPages) = bettingService.GetEventsOverview(page);
            md.Content = content;
            KeyboardMessage kb = new();
            if (totalPages > 1)
                kb.AddPaginationRow("赛事列表 ", page, totalPages);
            List<Button> buttons = [
                Button.CreateCmdButton("📋 赛事列表", "赛事列表"),
                Button.CreateCmdButton("📅 比赛列表", "比赛列表")
            ];
            if (!functionConfig.Betting_ReadOnly)
            {
                buttons.AddRange([
                    Button.CreateCmdButton("💰 预测领奖", "预测领奖"),
                    Button.CreateCmdButton("❓ 预测帮助", "预测帮助")
                ]);
            }
            kb.AppendButtonsWithNewRow(2, buttons);
            reply.Keyboard = kb;

            await botService.SendAsync(e, reply);
            return true;
        }

        if (!functionConfig.Betting_ReadOnly && e.Detail.StartsWith("我的预测"))
        {
            if (!await CheckUser(e, user, false, false))
            {
                return true;
            }

            int page = 1;
            string detail = e.Detail.Replace("我的预测", "").Trim();
            System.Text.RegularExpressions.Match match = GetFirstNumber().Match(detail);
            if (match.Success && int.TryParse(match.Value, out int p)) page = p;
            var (content, totalPages) = bettingService.GetMyBets(uid, -1, page);
            md.Content = content;
            KeyboardMessage kb = new();
            if (totalPages > 1)
                kb.AddPaginationRow("我的预测 ", page, totalPages);
            kb.AppendButtonsWithNewRow(2,
                Button.CreateCmdButton("💰 预测领奖", "预测领奖", enter: true),
                Button.CreateCmdButton("📋 赛事列表", "赛事列表"),
                Button.CreateCmdButton("📅 比赛列表", "比赛列表"),
                Button.CreateCmdButton("❓ 预测帮助", "预测帮助"));
            reply.Keyboard = kb;
            await botService.SendAsync(e, reply);
            return true;
        }

        if (!functionConfig.Betting_ReadOnly && e.Detail == "预测领奖")
        {
            if (!await CheckUser(e, user, false, false))
            {
                return true;
            }

            long total = bettingService.ClaimRewards(uid);
            if (total > 0)
            {
                if (user != null)
                {
                    user.Inventory.Credits += total;
                    sql.UpdateUser(user);
                }
                md.Content = $"领取成功！获得 {total} {General.GameplayEquilibriumConstant.InGameCurrency}。";
            }
            else
            {
                md.Content = "没有可领取的奖励。";
            }
            KeyboardMessage kb = new KeyboardMessage()
                .AppendButtons(2,
                    Button.CreateCmdButton("📜 我的预测", "我的预测"),
                    Button.CreateCmdButton("📋 赛事列表", "赛事列表"),
                    Button.CreateCmdButton("📅 比赛列表", "比赛列表"),
                    Button.CreateCmdButton("❓ 预测帮助", "预测帮助"));
            reply.Keyboard = kb;
            await botService.SendAsync(e, reply);
            return true;
        }

        if (!functionConfig.Betting_ReadOnly && e.Detail.StartsWith("预测"))
        {
            if (!await CheckUser(e, user, false, false))
            {
                return true;
            }

            string detail = e.Detail.Replace("预测", "").Trim();
            string[] parts = detail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !int.TryParse(parts[0], out int mid) || !long.TryParse(parts[^1], out long amt))
            {
                await botService.SendAsync(e, $"格式：预测 <比赛ID> <选项> <{General.GameplayEquilibriumConstant.InGameCurrency}数>\r\n选项：team1 / team2 / score:2:0 / mvp:选手ID");
                return true;
            }
            string option = string.Join(" ", parts[1..^1]).ToLower();

            if (amt < 1000)
            {
                md.Content = $"最低助力目标 1000 {General.GameplayEquilibriumConstant.InGameCurrency}。";
                await botService.SendAsync(e, new BotReply { Markdown = md });
                return true;
            }
            if (user!.Inventory.Credits < amt)
            {
                md.Content = $"{General.GameplayEquilibriumConstant.InGameCurrency}不足，你的{General.GameplayEquilibriumConstant.InGameCurrency}：{user.Inventory.Credits}。";
                await botService.SendAsync(e, new BotReply { Markdown = md });
                return true;
            }
            if (bettingService.PlaceBet(uid, mid, option, amt, out string error))
            {
                user.Inventory.Credits -= (int)amt;
                sql.UpdateUser(user);
                md.Content = $"{"助力目标".CreateCmdInput($"比赛详情 {mid}")}成功！{amt} {General.GameplayEquilibriumConstant.InGameCurrency}已扣除。";
            }
            else
            {
                md.Content = error;
            }

            KeyboardMessage kb = new();
            kb.AppendButtons(2,
                Button.CreateCmdButton("📜 我的预测", "我的预测"),
                Button.CreateCmdButton("📋 赛事列表", "赛事列表"),
                Button.CreateCmdButton("📅 比赛列表", "比赛列表"),
                Button.CreateCmdButton("❓ 预测帮助", "预测帮助"));

            if (reply.Markdown?.Content?.Contains("创建存档") ?? false)
            {
                kb.AppendButtons(2, Button.CreateCmdButton("⚙️ 创建存档", "创建存档"));
            }
            else
            {
                kb.AppendButtonsWithNewRow(2,
                    Button.CreateCmdButton("🔄 再次预测", e.Detail, enter: false),
                    Button.CreateCmdButton("🔍 比赛详情", $"比赛详情 {mid}"));
            }

            reply.Keyboard = kb;
            await botService.SendAsync(e, reply);
            return true;
        }

        // 确认结算：管理员确认自动预填的比赛结果，复用结算派彩路径并清除待确认标记
        if (e.Detail.StartsWith("确认结算"))
        {
            if (!await CheckUser(e, user, false)) return true;

            string detail = e.Detail.Replace("确认结算", "").Trim();
            if (!int.TryParse(detail, out int matchId))
            {
                await botService.SendAsync(e, "格式：确认结算 <比赛ID>");
                return true;
            }

            md.Content = bettingService.ConfirmPendingSettlement(matchId);
            reply.Keyboard = new KeyboardMessage().AppendButtons(2,
                Button.CreateCmdButton("🔍 比赛详情", $"比赛详情 {matchId}"),
                Button.CreateCmdButton("📅 比赛列表", "比赛列表"));
            await botService.SendAsync(e, reply);
            return true;
        }

        // 管理员结算
        if (e.Detail.StartsWith("结算比赛"))
        {
            if (!await CheckUser(e, user, false))
            {
                return true;
            }

            string detail = e.Detail.Replace("结算比赛", "").Trim();
            string[] parts = detail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && int.TryParse(parts[0], out int mid))
            {
                string winner = "", mResult = "";
                foreach (var p in parts[1..])
                {
                    if (p.StartsWith("winner=")) winner = p[7..];
                    if (p.StartsWith("result=")) mResult = p[7..];
                }
                string resultMsg = bettingService.SettleMatch(mid, winner, mResult);
                md.Content = resultMsg;
                reply.Keyboard = new KeyboardMessage()
                    .AppendButtons(2,
                        Button.CreateCmdButton("📋 赛事列表", "赛事列表"),
                        Button.CreateCmdButton("📅 比赛列表", "比赛列表"),
                        Button.CreateCmdButton("⚙️ 继续结算", "结算比赛 ", enter: false),
                        Button.CreateCmdButton("🔍 比赛详情", $"比赛详情 {mid}"));
                await botService.SendAsync(e, reply);
            }
            else
                await botService.SendAsync(e, "格式：结算比赛 <比赛ID> winner=team1 result=2:0");
            return true;
        }

        // 指令：创建赛事 <名称> <开始时间> <结束时间>
        // 示例：创建赛事 春季赛 2026-03-01 2026-03-10
        // 示例：创建赛事 总决赛 2026-04-01 2026-04-05 1001,1002
        if (e.Detail.StartsWith("创建赛事"))
        {
            if (!await CheckUser(e, user, false))
            {
                return true;
            }

            string detail = e.Detail.Replace("创建赛事", "").Trim();
            string[] parts = detail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                await botService.SendAsync(e, "格式：创建赛事 <名称> <开始时间> <结束时间>\r\n" +
                    "时间格式：yyyy-MM-dd HH:mm 或 yyyy-MM-dd（默认00:00）\r\n" +
                    "示例：创建赛事 春季赛 2026-03-01 2026-03-10\r\n" +
                    "示例：创建赛事 总决赛 2026-04-01 12:00 2026-04-05 18:00");
                return true;
            }

            string name = parts[0];
            if (name.Length > 100)
            {
                await botService.SendAsync(e, "赛事名称不能超过100个字符。");
                return true;
            }

            // 尝试解析时间（支持 yyyy-MM-dd 或 yyyy-MM-dd HH:mm）
            string startStr = parts[1] + (parts[2].Contains(':') ? " " + parts[2] : " 00:00");
            int nextIndex = parts[2].Contains(':') ? 3 : 2;
            string endStr = parts[nextIndex] + (parts.Length > nextIndex + 1 && parts[nextIndex + 1].Contains(':') ? " " + parts[nextIndex + 1] : " 00:00");

            if (!DateTime.TryParseExact(startStr, CheckDateTimeFormat, null, System.Globalization.DateTimeStyles.None, out DateTime startTime) ||
                !DateTime.TryParseExact(endStr, CheckDateTimeFormat, null, System.Globalization.DateTimeStyles.None, out DateTime endTime))
            {
                await botService.SendAsync(e, "时间格式错误，请使用 yyyy-MM-dd 或 yyyy-MM-dd HH:mm。");
                return true;
            }

            if (bettingService.CreateEvent(name, startTime, endTime, out string error, out long? newEventId))
            {
                md.Content = $"赛事创建成功！新赛事ID：{newEventId}";
                reply.Keyboard = new KeyboardMessage().AppendButtons(1, Button.CreateCmdButton("🔍 赛事详情", $"赛事详情 {newEventId}"));
            }
            else
            {
                md.Content = error;
            }
            await botService.SendAsync(e, reply);
            return true;
        }

        // 指令：创建比赛 <赛事ID> <队伍1> <队伍2> <阶段> <开始时间> [选项列表(key=value格式，空格分隔)]
        if (e.Detail.StartsWith("创建比赛"))
        {
            if (!await CheckUser(e, user, false))
            {
                return true;
            }

            string detail = e.Detail.Replace("创建比赛", "").Trim();
            string[] parts = detail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 6) // 最少需要 eventId, team1, team2, stage, start date, start time
            {
                await botService.SendAsync(e,
                    "格式：创建比赛 <赛事ID> <队伍1> <队伍2> <阶段> <开始时间> [选项列表(key=value格式，空格分隔)]\r\n" +
                    "开始时间格式：yyyy-MM-dd HH:mm（两段）\r\n" +
                    "可选参数：ddl=截止时间（需要用双引号包围），opts=选项列表（逗号分隔，默认team1_win,team2_win），team1_win=奖励率，team2_win=奖励率，prob=胜率\r\n" +
                    "示例：创建比赛 1 NAVI FaZe Quarter-final 2026-03-05 14:00\r\n" +
                    "示例：创建比赛 1 NAVI FaZe Quarter-final 2026-03-05 14:00 ddl=\"2026-03-05 13:55\" opts=team1_win,team2_win\r\n" +
                    "示例：创建比赛 1 G2 Vitality Semi-final 2026-04-02 18:00 ddl=\"2026-04-02 17:55\" opts=team1_win,team2_win team1_win=2.5 prob=0.6");
                return true;
            }

            if (!int.TryParse(parts[0], out int eventId))
            {
                await botService.SendAsync(e, "赛事ID必须为数字。");
                return true;
            }

            string team1 = parts[1];
            string team2 = parts[2];
            string stage = parts[3];

            // 开始时间（parts[4] + parts[5]）
            string startDate = parts[4];
            string startTime = parts[5];
            if (!DateTime.TryParseExact(startDate + " " + startTime, "yyyy-MM-dd HH:mm", null, System.Globalization.DateTimeStyles.None, out DateTime startDt))
            {
                await botService.SendAsync(e, "开始时间格式错误，请使用 yyyy-MM-dd HH:mm（日期和时间分为两段）。");
                return true;
            }

            // 解析剩余参数（从第6段开始，key=value格式，value支持双引号包围）
            int spaceCount = 0, idx = 0;
            for (; idx < detail.Length && spaceCount < 6; idx++)
            {
                if (detail[idx] == ' ') spaceCount++;
            }
            string paramString = detail[idx..].Trim();
            System.Text.RegularExpressions.MatchCollection matches = GetParamValue().Matches(paramString);

            // 解析剩余可选参数：key=value 格式，支持双引号包围含空格的value
            Dictionary<string, string> paramDict = new(StringComparer.OrdinalIgnoreCase);
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                string key = m.Groups[1].Value;
                string value = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
                if (!paramDict.TryAdd(key, value))
                {
                    await botService.SendAsync(e, $"参数 '{key}' 重复。");
                    return true;
                }
            }

            // 解析截止时间 (ddl=)，默认与开始时间相同
            DateTime deadlineDt = startDt;
            if (paramDict.TryGetValue("ddl", out string? ddlStr))
            {
                if (!DateTime.TryParseExact(ddlStr, "yyyy-MM-dd HH:mm", null, System.Globalization.DateTimeStyles.None, out deadlineDt))
                {
                    await botService.SendAsync(e, "截止时间格式错误，请使用 yyyy-MM-dd HH:mm。");
                    return true;
                }
            }

            // 解析选项列表 (opts=)
            string options = "team1_win,team2_win";
            if (paramDict.TryGetValue("opts", out string? optsStr))
            {
                options = optsStr; // 预期逗号分隔的列表
            }

            // 解析奖励率与胜率
            decimal? team1Odds = null, team2Odds = null, team1WinProbability = null;
            if (paramDict.TryGetValue("team1_win", out string? t1OddsStr))
            {
                if (decimal.TryParse(t1OddsStr, out decimal o1))
                {
                    team1Odds = o1;
                }
                else
                {
                    await botService.SendAsync(e, "team1_win 奖励率无效。");
                    return true;
                }
            }
            if (paramDict.TryGetValue("team2_win", out string? t2OddsStr))
            {
                if (decimal.TryParse(t2OddsStr, out decimal o2))
                {
                    team2Odds = o2;
                }
                else
                {
                    await botService.SendAsync(e, "team2_win 奖励率无效。");
                    return true;
                }
            }
            if (paramDict.TryGetValue("prob", out string? probStr))
            {
                if (decimal.TryParse(probStr, out decimal prob) && prob > 0 && prob < 1)
                {
                    team1WinProbability = prob;
                }
                else
                {
                    await botService.SendAsync(e, "胜率值无效，应为0~1之间的小数。");
                    return true;
                }
            }
            bool enableBet = true;
            if (paramDict.TryGetValue("be", out string? be))
            {
                if (be == "0" || be == "1") enableBet = be == "1";
                else
                {
                    await botService.SendAsync(e, "enabled 值必须为 0 或 1。");
                    return true;
                }
            }
            // 如果队伍包含TBD，则默认不可预测
            if (team1 == "TBD" || team2 == "TBD")
            {
                enableBet = false;
            }

            if (bettingService.CreateMatch(eventId, team1, team2, stage, startDt, deadlineDt, options, team1Odds, team2Odds, team1WinProbability, enableBet, out string error, out long? newMatchId))
            {
                md.Content = $"比赛创建成功！新比赛ID：{newMatchId}";
                reply.Keyboard = new KeyboardMessage().AppendButtons(2, [
                    Button.CreateCmdButton("🔍 比赛详情", $"比赛详情 {newMatchId}"),
                    Button.CreateCmdButton("🖌 修改比赛", $"修改比赛 {newMatchId}")
                ]);
            }
            else
            {
                md.Content = error;
            }
            await botService.SendAsync(e, reply);
            return true;
        }

        // 指令：修改比赛 <比赛ID> [参数=值 ...]
        if (e.Detail.StartsWith("修改比赛"))
        {
            if (!await CheckUser(e, user, false))
            {
                return true;
            }

            string detail = e.Detail.Replace("修改比赛", "").Trim();
            string[] parts = detail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                await botService.SendAsync(e,
                    "格式：修改比赛 <比赛ID> [参数=值]\r\n" +
                    "可用参数：t1od=<奖励率> t2od=<奖励率> st=<开始时间> ddl=<截止时间> des=<描述>\r\n" +
                    "时间格式：yyyy-MM-dd HH:mm\r\n" +
                    "示例：修改比赛 10 t1od=2.8 ddl=2026-05-12 18:00 des=\"决赛，Bo3\"");
                return true;
            }

            if (!int.TryParse(parts[0], out int matchId))
            {
                await botService.SendAsync(e, "比赛ID必须为数字。");
                return true;
            }

            // 解析剩余参数 key=value，支持引号包裹的含空格值
            UpdateMatchRequest request = new() { Uid = uid, MatchId = matchId };
            string remaining = string.Join(" ", parts[1..]);
            // 使用正则匹配 key=value，value 可能带双引号
            System.Text.RegularExpressions.MatchCollection matches = GetParamValue().Matches(remaining);
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                string key = m.Groups[1].Value;
                string val = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;

                switch (key)
                {
                    case "t1od":
                        if (decimal.TryParse(val, out decimal t1od)) request.Team1WinOdds = t1od;
                        else { await botService.SendAsync(e, "t1od 值无效。"); return true; }
                        break;
                    case "t2od":
                        if (decimal.TryParse(val, out decimal t2od)) request.Team2WinOdds = t2od;
                        else { await botService.SendAsync(e, "t2od 值无效。"); return true; }
                        break;
                    case "prob":
                        if (decimal.TryParse(val, out decimal prob)) request.Team1WinProbability = prob;
                        else { await botService.SendAsync(e, "prob 值无效。"); return true; }
                        break;
                    case "st":
                        if (DateTime.TryParse(val, out DateTime st)) request.StartTime = st;
                        else { await botService.SendAsync(e, "开始时间格式错误，请用 yyyy-MM-dd HH:mm。"); return true; }
                        break;
                    case "ddl":
                        if (DateTime.TryParse(val, out DateTime ddl)) request.BetDeadline = ddl;
                        else { await botService.SendAsync(e, "截止时间格式错误，请用 yyyy-MM-dd HH:mm。"); return true; }
                        break;
                    case "des":
                        request.Description = val; // 允许空字符串清空描述
                        break;
                    case "result":
                        request.Result = val;
                        break;
                    case "stage":
                        request.Stage = val;
                        break;
                    case "t1":
                        request.Team1 = val;
                        break;
                    case "t2":
                        request.Team2 = val;
                        break;
                    case "be":
                        if (val == "0" || val == "1")
                            request.BettingEnabled = val == "1";
                        else
                        {
                            await botService.SendAsync(e, "enabled 值必须为 0 或 1。");
                            return true;
                        }
                        break;
                    default:
                        await botService.SendAsync(e, $"未知参数：{key}");
                        return true;
                }
            }

            if (!bettingService.UpdateMatch(request, out string error))
            {
                md.Content = error;
                reply.Keyboard = new KeyboardMessage().AppendButtons(1, Button.CreateCmdButton("🖌 重新修改", e.Detail));
                await botService.SendAsync(e, reply);
                return true;
            }
            md.Content = $"比赛 {request.MatchId} 属性修改成功。";
            reply.Keyboard = new KeyboardMessage()
                .AppendButtons(2,
                    Button.CreateCmdButton("📋 赛事列表", "赛事列表"),
                    Button.CreateCmdButton("📅 比赛列表", "比赛列表"),
                    Button.CreateCmdButton("🔍 比赛详情", $"比赛详情 {matchId}"));
            await botService.SendAsync(e, reply);
            return true;
        }

        // 取消比赛指令
        if (e.Detail.StartsWith("取消比赛"))
        {
            if (!await CheckUser(e, user, false))
            {
                return true;
            }

            string detail = e.Detail.Replace("取消比赛", "").Trim();
            if (!int.TryParse(detail, out int matchId))
            {
                await botService.SendAsync(e, "格式：取消比赛 <比赛ID>");
                return true;
            }

            bettingService.CancelMatch(matchId, out string msg);
            md.Content = msg;
            reply.Keyboard = new KeyboardMessage()
                .AppendButtons(2,
                    Button.CreateCmdButton("📋 赛事列表", "赛事列表"),
                    Button.CreateCmdButton("📅 比赛列表", "比赛列表"),
                    Button.CreateCmdButton("🔍 比赛详情", $"比赛详情 {matchId}"),
                    Button.CreateCmdButton("💰 预测领奖", "预测领奖"));
            await botService.SendAsync(e, reply);
            return true;
        }

        if (e.Detail.StartsWith("推迟比赛"))
        {
            if (!await CheckUser(e, user, false))
            {
                return true;
            }

            string detail = e.Detail.Replace("推迟比赛", "").Trim();
            string[] parts = detail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[0], out int matchId) || !int.TryParse(parts[1], out int minutes) || minutes <= 0)
            {
                await botService.SendAsync(e, "格式：推迟比赛 <比赛ID> <分钟数>\n示例：推迟比赛 123 30");
                return true;
            }

            // 获取当前比赛时间
            if (!bettingService.GetMatchTimes(matchId, out DateTime oldStart, out DateTime oldDeadline, out int status, out string error))
            {
                md.Content = error;
                await botService.SendAsync(e, new BotReply { Markdown = md });
                return true;
            }

            if (status == 2)
            {
                md.Content = "比赛已结束，无法推迟。";
                await botService.SendAsync(e, new BotReply { Markdown = md });
                return true;
            }

            DateTime newStart = oldStart.AddMinutes(minutes);
            DateTime newDeadline = oldDeadline.AddMinutes(minutes);

            UpdateMatchRequest request = new()
            {
                MatchId = matchId,
                StartTime = newStart,
                BetDeadline = newDeadline
            };

            if (bettingService.UpdateMatch(request, out error))
            {
                md.Content = $"比赛 {matchId} 已推迟 {minutes} 分钟。\n新开始时间：{newStart:yyyy-MM-dd HH:mm}\n新截止时间：{newDeadline:yyyy-MM-dd HH:mm}";
                reply.Keyboard = new KeyboardMessage()
                    .AppendButtons(2,
                        Button.CreateCmdButton("📋 赛事列表", "赛事列表"),
                        Button.CreateCmdButton("📅 比赛列表", "比赛列表"),
                        Button.CreateCmdButton("🔍 比赛详情", $"比赛详情 {matchId}"));
            }
            else
            {
                md.Content = error;
            }

            await botService.SendAsync(e, reply);
            return true;
        }

        // 提前比赛
        if (e.Detail.StartsWith("提前比赛"))
        {
            if (!await CheckUser(e, user, false))
            {
                return true;
            }

            string detail = e.Detail.Replace("提前比赛", "").Trim();
            string[] parts = detail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[0], out int matchId) || !int.TryParse(parts[1], out int minutes) || minutes <= 0)
            {
                await botService.SendAsync(e, "格式：提前比赛 <比赛ID> <分钟数>\n示例：提前比赛 123 30");
                return true;
            }

            // 获取当前比赛时间
            if (!bettingService.GetMatchTimes(matchId, out DateTime oldStart, out DateTime oldDeadline, out int status, out string error))
            {
                md.Content = error;
                await botService.SendAsync(e, new BotReply { Markdown = md });
                return true;
            }

            if (status == 2)
            {
                md.Content = "比赛已结束，无法提前。";
                await botService.SendAsync(e, new BotReply { Markdown = md });
                return true;
            }

            DateTime newStart = oldStart.AddMinutes(-minutes);
            DateTime newDeadline = oldDeadline.AddMinutes(-minutes);

            if (newDeadline < ChinaStandardTime.Now)
            {
                md.Content = $"⚠️ 注意：提前后截止时间为 {newDeadline:yyyy-MM-dd HH:mm}，已经早于当前时间，预测将立即锁定。\n";
            }

            UpdateMatchRequest request = new()
            {
                MatchId = matchId,
                StartTime = newStart,
                BetDeadline = newDeadline
            };

            if (bettingService.UpdateMatch(request, out error))
            {
                md.Content += $"比赛 {matchId} 已提前 {minutes} 分钟。\n新开始时间：{newStart:yyyy-MM-dd HH:mm}\n新截止时间：{newDeadline:yyyy-MM-dd HH:mm}";
                reply.Keyboard = new KeyboardMessage()
                    .AppendButtons(2,
                        Button.CreateCmdButton("📋 赛事列表", "赛事列表"),
                        Button.CreateCmdButton("📅 比赛列表", "比赛列表"),
                        Button.CreateCmdButton("🔍 比赛详情", $"比赛详情 {matchId}"));
            }
            else
            {
                md.Content = error;
            }

            await botService.SendAsync(e, reply);
            return true;
        }

        return false;
    }

    private async Task<bool> HandleBettingScopeFilterInput(IBotMessage e, User? user)
    {
        string detail = e.Detail.Trim();
        bool isScopeCommand = detail == "赛事筛选帮助"
            || detail == "查看赛事筛选"
            || detail.StartsWith("搜索赛事筛选")
            || detail.StartsWith("添加赛事筛选")
            || detail.StartsWith("移除赛事筛选")
            || detail == "清空赛事筛选"
            || detail == "预览赛事筛选";
        if (!isScopeCommand)
        {
            return false;
        }

        if (!await CheckUser(e, user, false)) return true;

        if (detail == "赛事筛选帮助")
        {
            await botService.SendAsync(e,
                "赛事筛选指令：\n" +
                "搜索赛事筛选 <关键词>\n" +
                "添加赛事筛选 <序号>\n" +
                "添加赛事筛选 <league|serie|tournament> <id或slug>\n" +
                "移除赛事筛选 <league|serie|tournament> <id或slug>\n" +
                "查看赛事筛选\n" +
                "清空赛事筛选\n" +
                "预览赛事筛选");
            return true;
        }

        if (detail == "查看赛事筛选")
        {
            await botService.SendAsync(e, RenderScopeFilters());
            return true;
        }

        if (detail.StartsWith("搜索赛事筛选"))
        {
            string keyword = detail["搜索赛事筛选".Length..].Trim();
            if (string.IsNullOrWhiteSpace(keyword))
            {
                await botService.SendAsync(e, "格式：搜索赛事筛选 <关键词>\n示例：搜索赛事筛选 blast");
                return true;
            }

            IReadOnlyList<ExternalEsportsScope> scopes = await esportsSyncService.SearchFilterScopesAsync(keyword);
            memoryCache.Set(ScopeSearchCacheKey(e), scopes, TimeSpan.FromMinutes(EsportsScopeSearchCacheMinutes));
            await botService.SendAsync(e, RenderScopeSearchResults(keyword, scopes));
            return true;
        }

        if (detail.StartsWith("添加赛事筛选"))
        {
            await HandleAddScopeFilter(e, detail["添加赛事筛选".Length..].Trim());
            return true;
        }

        if (detail.StartsWith("移除赛事筛选"))
        {
            await HandleRemoveScopeFilter(e, detail["移除赛事筛选".Length..].Trim());
            return true;
        }

        if (detail == "清空赛事筛选")
        {
            esportsSyncService.ClearScopeFilters();
            await botService.SendAsync(e, "已清空赛事筛选。");
            return true;
        }

        if (detail == "预览赛事筛选")
        {
            if (!WillGoOn(functionConfig.BettingAutomation, user))
            {
                await botService.SendAsync(e, "赛事自动化正在维护，请稍后再试。");
                return true;
            }

            IReadOnlyList<ExternalEsportsMatch> matches = await esportsSyncService.PreviewFilteredMatchesAsync();
            await botService.SendAsync(e, RenderScopePreview(matches));
            return true;
        }

        return false;
    }

    private async Task HandleAddScopeFilter(IBotMessage e, string argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            await botService.SendAsync(e, "格式：添加赛事筛选 <序号>\n或：添加赛事筛选 <league|serie|tournament> <id或slug>");
            return;
        }

        ExternalEsportsScope? selected = null;
        if (int.TryParse(argument, out int index))
        {
            if (!memoryCache.TryGetValue(ScopeSearchCacheKey(e), out IReadOnlyList<ExternalEsportsScope>? cached) || cached is null)
            {
                await botService.SendAsync(e, "请先发送：搜索赛事筛选 <关键词>");
                return;
            }

            selected = cached.ElementAtOrDefault(index - 1);
            if (selected is null)
            {
                await botService.SendAsync(e, $"搜索结果中没有序号 {index}。");
                return;
            }
        }
        else
        {
            string[] parts = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
            {
                await botService.SendAsync(e, "格式：添加赛事筛选 <league|serie|tournament> <id或slug>");
                return;
            }
            string type = EsportsSyncService.NormalizeScopeType(parts[0]);
            selected = new(type, parts[1], parts[1]);
        }

        if (string.IsNullOrWhiteSpace(selected.Type) || !esportsSyncService.AddScopeFilter(selected.Type, selected.Value))
        {
            await botService.SendAsync(e, "赛事筛选类型必须是 league、serie 或 tournament。");
            return;
        }

        await botService.SendAsync(e, $"已添加赛事筛选：{selected.Type} {selected.Value}\n{RenderScopeFilters()}");
    }

    private async Task HandleRemoveScopeFilter(IBotMessage e, string argument)
    {
        string[] parts = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            await botService.SendAsync(e, "格式：移除赛事筛选 <league|serie|tournament> <id或slug>");
            return;
        }

        string type = EsportsSyncService.NormalizeScopeType(parts[0]);
        if (string.IsNullOrWhiteSpace(type) || !esportsSyncService.RemoveScopeFilter(type, parts[1]))
        {
            await botService.SendAsync(e, "赛事筛选类型必须是 league、serie 或 tournament。");
            return;
        }

        await botService.SendAsync(e, $"已移除赛事筛选：{type} {parts[1]}\n{RenderScopeFilters()}");
    }

    private string RenderScopeFilters()
    {
        var (league, serie, tournament) = esportsSyncService.GetScopeFilters();
        return "当前赛事筛选：\n" +
            $"league: {RenderScopeValue(league)}\n" +
            $"serie: {RenderScopeValue(serie)}\n" +
            $"tournament: {RenderScopeValue(tournament)}";
    }

    private static string RenderScopeValue(string value)
    {
        string[] values = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return values.Length == 0 ? "未设置" : string.Join(", ", values);
    }

    private static string RenderScopeSearchResults(string keyword, IReadOnlyList<ExternalEsportsScope> scopes)
    {
        if (scopes.Count == 0)
        {
            return $"未找到赛事筛选候选项：{keyword}";
        }

        List<string> lines = [$"搜索结果：{keyword}"];
        for (int i = 0; i < scopes.Count; i++)
        {
            ExternalEsportsScope scope = scopes[i];
            lines.Add($"{i + 1}. [{scope.Type}] {scope.Name}");
            string parent = string.IsNullOrWhiteSpace(scope.ParentName) ? "" : $" parent={scope.ParentName}";
            string id = scope.Id.HasValue ? $" id={scope.Id}" : "";
            lines.Add($"   value={scope.Value}{id}{parent}");
            lines.Add($"   添加：添加赛事筛选 {i + 1}");
        }
        return string.Join('\n', lines);
    }

    private static string RenderScopePreview(IReadOnlyList<ExternalEsportsMatch> matches)
    {
        if (matches.Count == 0)
        {
            return "当前筛选没有可抓取的未开始比赛。";
        }

        List<string> lines = [$"当前筛选可抓取 {matches.Count} 场未开始比赛："];
        foreach (ExternalEsportsMatch match in matches.OrderBy(m => m.StartTime).Take(5))
        {
            DateTime start = ChinaStandardTime.FromUtc(match.StartTime);
            lines.Add($"{start:yyyy-MM-dd HH:mm} {match.Team1Name} vs {match.Team2Name} [{match.Stage}]");
        }
        if (matches.Count > 5)
        {
            lines.Add($"还有 {matches.Count - 5} 场未显示。");
        }
        return string.Join('\n', lines);
    }

    private static string ScopeSearchCacheKey(IBotMessage e)
        => $"pandascore:scope-search:last:{e.AuthorOpenId}";
}
