using CandyCottage.CandyBot.Models;
using Milimoe.FunGame.Core.Api.Utility;
using Milimoe.FunGame.Core.Entity;

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

            if (e.Detail.StartsWith("生成API秘钥"))
            {
                if (!await CheckUser(e, user, false, false))
                {
                    return true;
                }

                if (e.IsGroup)
                {
                    md.Content = "该指令只能私聊哦～";
                    await botService.SendAsync(e, reply);
                    return true;
                }

                string name = e.Detail.Replace("生成API秘钥", "").Trim();
                if (name == "")
                {
                    md.Content = "未提供秘钥名称。";
                    await botService.SendAsync(e, reply);
                    return true;
                }

                // 此处开始 user 不为空
                string username = user!.Username;

                // 检查秘钥数量是否超过5个
                string script = "Select Count(TokenID) From ApiTokens Where Reference1 = @Username";
                sql.Parameters["@Username"] = username;
                sql.ExecuteDataSet(script);
                if (sql.Success && int.TryParse(sql.DataSet.Tables[0].Rows[0][0].ToString(), out int count))
                {
                    if (count >= 5)
                    {
                        md.Content = $"你的 API 秘钥数量已达上限，请酌情{"删除秘钥".CreateCmdInput("API秘钥列表")}后再试。";
                    }
                    else
                    {
                        string tokenId = $"{user!.Username}-{Verification.CreateVerifyCode(Milimoe.FunGame.Core.Library.Constant.VerifyCodeType.MixVerifyCode, 6)}";
                        string secret = $"cd-{Guid.NewGuid():N}";
                        string secretHash = secret.Encrypt(candyBotConfig.ApiTokenSalt);
                        sql.Execute(Milimoe.FunGame.Core.Library.SQLScript.Common.ApiTokens.Insert_APIToken(sql, tokenId, secretHash, user!.Username, name));
                        if (sql.Success)
                        {
                            md.Content = $"生成成功，请立即复制秘钥，30 秒后机器人会撤回。";
                            await botService.SendAsync(e, reply, 1);
                            md.Content = secret;
                            SendMessageResult? result = await botService.SendAsync(e, reply, 2);
                            if (result != null)
                            {
                                _ = Task.Run(async () =>
                                {
                                    await Task.Delay(30 * 1000);
                                    // C2C撤回需要两个参数：1、消息ID 2、目标用户OpenID
                                    // 如果是群撤回，第二个参数是群的OpenID
                                    await botService.RecallMessageAsync(new C2CMessage()
                                    {
                                        Id = result.Id,
                                        Author = new()
                                        {
                                            Id = e.AuthorOpenId
                                        }
                                    });
                                });
                            }
                            return true;
                        }
                    }
                }

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
