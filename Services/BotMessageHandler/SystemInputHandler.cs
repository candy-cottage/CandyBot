using System.Text;
using CandyCottage.CandyBot.Models;
using Milimoe.FunGame.Core.Entity;
using Milimoe.FunGame.Core.Interface.Base;
using Milimoe.FunGame.Core.Model;

namespace CandyCottage.CandyBot.Services;

public partial class CandyBotService
{
    public async Task<bool> HandleSystemInput(IBotMessage e, User? user)
    {
        long uid = user?.Id ?? 0;
        MarkdownMessage md = new() { Content = _busy };
        BotReply reply = new() { Markdown = md };

        try
        {
            if (e.Detail == "系统初始化")
            {
                if (sqlInitService.WillInitialize())
                {
                    sqlInitService.Initialize(e.AuthorOpenId, out string msg);
                    md.Content = msg;
                    await botService.SendAsync(e, reply);
                }
                return true;
            }

            if (e.Detail == "令牌列表" || e.Detail == "我的令牌")
            {
                if (!await CheckUser(e, user, false, false)) return true;

                if (e.IsGroup)
                {
                    await botService.SendAsync(e, "该指令只能私聊哦～");
                    return true;
                }

                md.Content = userAPITokenService.ShowTokenList(user!.Username);
                await botService.SendAsync(e, reply);
                return true;
            }

            if (e.Detail.StartsWith("删除令牌"))
            {
                if (!await CheckUser(e, user, false, false)) return true;

                if (e.IsGroup)
                {
                    await botService.SendAsync(e, "该指令只能私聊哦～");
                    return true;
                }

                string param = e.Detail["删除令牌".Length..].Trim();
                if (string.IsNullOrEmpty(param))
                {
                    md.Content = "请指定要删除的令牌名称或ID。";
                    await botService.SendAsync(e, reply);
                    return true;
                }

                md.Content = userAPITokenService.DeleteToken(user!.Username, param);
                await botService.SendAsync(e, reply);
                return true;
            }

            if (e.Detail.StartsWith("生成令牌"))
            {
                if (!await CheckUser(e, user, false, false))
                {
                    return true;
                }

                if (e.IsGroup)
                {
                    await botService.SendAsync(e, "该指令只能私聊哦～");
                    return true;
                }

                string name = e.Detail.Replace("生成令牌", "").Trim();
                if (name == "")
                {
                    await botService.SendAsync(e, "未提供令牌名称。");
                    return true;
                }

                int result = userAPITokenService.CreateApiTokenSecret(user!.Username, name, out string secret);
                if (result == 0)
                {
                    md.Content = $"生成成功，请立即复制令牌，机器人将在 30 秒后撤回消息。";
                    await botService.SendAsync(e, reply, 1);
                    md.Content = secret;
                    SendMessageResult? sendResult = await botService.SendAsync(e, reply, 2);
                    if (sendResult != null)
                    {
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(30 * 1000);
                            // C2C撤回需要两个参数：1、消息ID 2、目标用户OpenID
                            // 如果是群撤回，第二个参数是群的OpenID
                            await botService.RecallMessageAsync(new C2CMessage()
                            {
                                Id = sendResult.Id,
                                Author = new()
                                {
                                    Id = e.AuthorOpenId
                                }
                            });
                        });
                    }
                    return true;
                }
                else if (result == 2)
                {
                    md.Content = $"你的 API 令牌数量已达上限，请酌情{"删除令牌".CreateCmdInput("令牌列表")}后再试。";
                }
                else if (result == 3)
                {
                    md.Content = $"已存在同名令牌，请先删除。{"令牌列表".CreateCmdInput()}";
                }

                await botService.SendAsync(e, reply);
                return true;
            }

            if (e.Detail == "任务计划列表")
            {
                if (!await CheckUser(e, user)) return true;

                IReadOnlyList<IScheduledTask> tasks = taskManager.GetAllTasks();
                if (tasks.Count == 0)
                {
                    await botService.SendAsync(e, "系统中不存在任何任务计划。");
                    return true;
                }

                StringBuilder sb = new();
                sb.AppendLine("**📋 任务计划列表**\n");
                foreach (IScheduledTask task in tasks)
                {
                    bool isPaused = taskManager.IsPaused(task.Name);
                    string status = isPaused ? "⏸️ 已暂停" : "▶️ 运行中";
                    string nextRun = task.NextRun == DateTime.MaxValue ? "未计划" : task.NextRun.ToString("yyyy-MM-dd HH:mm:ss");
                    string lastRun = task.LastRun?.ToString("yyyy-MM-dd HH:mm:ss") ?? "未执行";
                    string error = task.Error != null ? $" ⚠️ 上次出错" : "";

                    sb.AppendLine($"**{task.Name}**");
                    sb.AppendLine($"- 任务状态：{status}{error}");
                    sb.AppendLine($"- 下次执行：{nextRun}");
                    sb.AppendLine($"- 上次执行：{lastRun}");
                    sb.AppendLine($"操作：{(isPaused ? "继续运行".CreateCmdInput($"继续任务计划 {task.Name}") : "暂停运行".CreateCmdInput($"暂停任务计划 {task.Name}"))}" +
                        $" | {"查看详情".CreateCmdInput($"查询任务计划 {task.Name}")}");
                }
                sb.AppendLine("> 使用 `查询任务计划 <任务名>` 查看详细信息。");

                md.Content = sb.ToString();
                await botService.SendAsync(e, reply);
                return true;
            }

            if (e.Detail.StartsWith("查询任务计划"))
            {
                if (!await CheckUser(e, user, true, true)) return true;

                string taskName = e.Detail["查询任务计划".Length..].Trim();
                if (string.IsNullOrEmpty(taskName))
                {
                    await botService.SendAsync(e, "请指定要查询的任务名称。");
                    return true;
                }

                IScheduledTask? task = taskManager.GetAllTasks().FirstOrDefault(t => t.Name.Equals(taskName, StringComparison.OrdinalIgnoreCase));
                if (task is null)
                {
                    await botService.SendAsync(e, $"未找到名为“{taskName}”的任务计划。");
                    return true;
                }

                bool isPaused = taskManager.IsPaused(task.Name);
                string status = isPaused ? "⏸️ 已暂停" : "▶️ 运行中";
                string nextRun = task.NextRun == DateTime.MaxValue ? "未计划" : task.NextRun.ToString("yyyy-MM-dd HH:mm:ss");
                string lastRun = task.LastRun?.ToString("yyyy-MM-dd HH:mm:ss") ?? "未执行";
                string executedTime = task.ExecutedTimeSpan.TotalMilliseconds > 0 ? $"{task.ExecutedTimeSpan.TotalMilliseconds:F0} ms" : "未知";
                string errorMsg = task.Error != null ? $"{task.Error.Message}" : "";

                StringBuilder sb = new();
                sb.AppendLine($"📌 任务详情：{task.Name}");
                sb.AppendLine();
                sb.AppendLine("| 属性 | 值 |");
                sb.AppendLine("|------|----|");
                sb.AppendLine($"| 状态 | {status} |");
                sb.AppendLine($"| 下次执行 | {nextRun} |");
                sb.AppendLine($"| 上次执行 | {lastRun} |");
                sb.AppendLine($"| 上次耗时 | {executedTime} |");

                // 任务类型特定信息
                if (task is RecurringTask rt)
                {
                    sb.AppendLine($"| 任务类型 | 周期性任务 |");
                    sb.AppendLine($"| 执行间隔 | {rt.Interval.TotalMinutes:F0} 分钟 |");
                }
                else if (task is ScheduledTask st)
                {
                    // TimeOfDay 是 TimeSpan，格式化为 "HH:mm:ss"
                    string scheduleTime = st.TimeOfDay.ToString(@"hh\:mm\:ss");
                    sb.AppendLine($"| 任务类型 | 每日计划任务 |");
                    sb.AppendLine($"| 每日执行时间 | {scheduleTime} |");
                }

                if (!string.IsNullOrEmpty(errorMsg))
                {
                    sb.AppendLine($"| 最后错误 | {errorMsg} |");
                    if (task.Error?.StackTrace != null)
                    {
                        string stack = task.Error.StackTrace.Length > 200 ? task.Error.StackTrace[..200] + "…" : task.Error.StackTrace;
                        sb.AppendLine($"| 堆栈信息 | `{stack.Replace("|", "\\|")}` |");
                    }
                }

                md.Content = sb.ToString();
                await botService.SendAsync(e, reply);
                return true;
            }

            if (e.Detail.StartsWith("暂停任务计划") || e.Detail.StartsWith("继续任务计划"))
            {
                if (!await CheckUser(e, user))
                {
                    return true;
                }

                string name = e.Detail.Replace("暂停任务计划", "").Replace("继续任务计划", "").Trim();
                bool goon = e.Detail.StartsWith("继续");
                bool isPaused = taskManager.IsPaused(name);
                if (goon)
                {
                    if (!isPaused)
                    {
                        md.Content = "该任务未处于暂停状态。";
                    }
                    else if (taskManager.ResumeTask(name))
                    {
                        md.Content = "恢复该任务计划成功。";
                    }
                    else
                    {
                        md.Content = "无法恢复该任务。";
                    }
                }
                else
                {
                    if (isPaused)
                    {
                        md.Content = "该任务已处于暂停状态。";
                    }
                    else if (taskManager.PauseTask(name))
                    {
                        md.Content = "暂停该任务计划成功。";
                    }
                    else
                    {
                        md.Content = "无法暂停该任务。";
                    }
                }
                await botService.SendAsync(e, reply);
                return true;
            }
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Error)) logger.LogError("Error: {ex}", ex);
            await botService.SendAsync(e, _busy);
            return true;
        }

        return false;
    }
}
