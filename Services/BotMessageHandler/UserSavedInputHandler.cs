using System.Data;
using System.Text;
using CandyCottage.CandyBot.Models;
using Milimoe.FunGame.Core.Entity;
using ProjectRedbud.FunGame.SQLQueryExtension;

namespace CandyCottage.CandyBot.Services;

public partial class CandyBotService
{
    private const string changeNickNameTip = "昵称修改须知：\r\n1、昵称长度为2-15个字符，可以与他人重复。\r\n2、昵称需要经过审核，不能包含敏感内容和不常见的特殊字符。\r\n3、昵称将在一天内审核完毕，请勿多次提交修改。";

    public async Task<bool> HandleUserSavedInput(IBotMessage e, User? user)
    {
        long uid = user?.Id ?? 0;
        MarkdownMessage md = new() { Content = _busy };
        BotReply reply = new() { Markdown = md };

        if (!WillGoOn(functionConfig.UserSaved, user))
        {
            md.Content = "用户系统正在维护，请稍后再试。";
            await botService.SendAsync(e, reply);
            return true;
        }

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

            if (e.Detail == "创建存档")
            {
                if (user != null)
                {
                    md.Content = "你已有存档，无需重复创建。";
                }
                else if (e.AuthorOpenId.Trim() == "")
                {
                    md.Content = "OpenID 无效，请与服务器管理员取得联系。";
                }
                else if (sql.UsernameExists(e.AuthorOpenId))
                {
                    md.Content = "你似乎已经创建过存档，请与服务器管理员取得联系。";
                }
                else
                {
                    userService.CreateSaved(e.AuthorOpenId, out string msg);
                    md.Content = msg;
                    reply.Keyboard = new KeyboardMessage().AppendButtons(2,
                        Button.CreateCmdButton("🍬 糖果屋", "糖果屋"),
                        Button.CreateCmdButton("🖌 修改昵称", "修改昵称为", false));
                }

                await botService.SendAsync(e, reply);
                return true;
            }

            if (functionConfig.UserRename && e.Detail.StartsWith("修改昵称为"))
            {
                if (!await CheckUser(e, user, false, false))
                {
                    return true;
                }

                string newName = e.Detail["修改昵称为".Length..].Trim();
                if (!string.IsNullOrEmpty(newName))
                {
                    md.Content = $"{changeNickNameTip}\r\n了解以上须知后，点击下方按钮确认提交修改。";
                    reply.Keyboard = new KeyboardMessage().AppendButtons(1, Button.CreateCmdButton("✅ 确认修改昵称", "确认修改昵称为" + newName, false));
                }
                else
                {
                    md.Content = "昵称不能为空，请重新输入。";
                    reply.Keyboard = new KeyboardMessage().AppendButtons(1, Button.CreateCmdButton("🖌 重新输入", e.Detail, false));
                }
                await botService.SendAsync(e, reply);
                return true;
            }

            if (functionConfig.UserRename && e.Detail.StartsWith("确认修改昵称为"))
            {
                if (!await CheckUser(e, user, false, false))
                {
                    return true;
                }

                string newName = e.Detail["确认修改昵称为".Length..].Trim();
                if (!string.IsNullOrEmpty(newName))
                {
                    md.Content = userService.SubmitNicknameChange(user!.Id, user.NickName, newName);
                    reply.Keyboard = new KeyboardMessage().AppendButtons(1, Button.CreateCmdButton("❌ 取消修改昵称", "取消修改昵称", false));
                }
                else
                {
                    md.Content = "昵称不能为空，请重新输入。";
                    reply.Keyboard = new KeyboardMessage().AppendButtons(1, Button.CreateCmdButton("🖌 重新输入", e.Detail, false));
                }
                await botService.SendAsync(e, reply);
                return true;
            }

            if (functionConfig.UserRename && e.Detail == "取消修改昵称")
            {
                if (!await CheckUser(e, user, false, false))
                {
                    return true;
                }
                md.Content = userService.CancelNicknameChange(uid);
                reply.Keyboard = new KeyboardMessage().AppendButtons(1, Button.CreateCmdButton("🖌 重新申请", "确认修改昵称为", false));
                await botService.SendAsync(e, reply);
                return true;
            }

            if (e.Detail.StartsWith("审核昵称"))
            {
                if (!await CheckUser(e, user, false))
                    return true;

                int page = 1;
                int pageSize = 5;
                int total = userService.GetPendingCount();
                if (total == 0)
                {
                    await botService.SendAsync(e, "当前没有待审核的昵称修改申请。");
                    return true;
                }

                DataTable requests = userService.GetPendingRequests(page, pageSize);
                StringBuilder sb = new();
                sb.AppendLine($"📋 **待审核昵称列表**（共 {total} 条，第 {page}/{Math.Ceiling(total / (double)pageSize)} 页）\n");
                foreach (DataRow req in requests.Rows)
                {
                    long id = Convert.ToInt64(req["Id"]);
                    string username = req["Username"]?.ToString() ?? "未知";
                    string currentName = req["CurrentName"]?.ToString() ?? "";
                    string newName = req["NewName"]?.ToString() ?? "";
                    DateTime submitTime = Convert.ToDateTime(req["SubmitTime"]);
                    int count = Convert.ToInt32(req["SubmitCount"]);
                    sb.AppendLine($"待审 #{id} | UID：{req["UserId"]}");
                    sb.AppendLine($"当前昵称：{currentName} => 申请：{newName}");
                    sb.AppendLine($"提交时间：{submitTime:yyyy-MM-dd HH:mm} | 提交次数：{count}");
                    sb.AppendLine($"操作：【{$"通过昵称 {id}".CreateCmdInput()}】或【{$"拒绝昵称 {id} [原因]".CreateCmdInput($"拒绝昵称 {id}")}】\n");
                }
                md.Content = sb.ToString();
                reply.Keyboard = new KeyboardMessage().AddPaginationRow("审核昵称", page, total);
                await botService.SendAsync(e, reply);
                return true;
            }

            if (e.Detail.StartsWith("通过昵称"))
            {
                if (!await CheckUser(e, user, false))
                    return true;

                string[] parts = e.Detail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !long.TryParse(parts[1], out long requestId))
                {
                    await botService.SendAsync(e, "格式错误，请使用：`通过昵称 <申请ID>`");
                    return true;
                }

                bool success = userService.ApproveNicknameChange(requestId, out long requestUserId, uid);
                md.Content = success ? $"✅ 已通过申请 ID {requestId}，用户昵称已更新。" : "操作失败，请检查申请ID是否正确或申请尚未处于待审核状态。";
                reply.Keyboard = new KeyboardMessage().AppendButtons(1, Button.CreateCmdButton("🔧 审核昵称", "审核昵称"));
                await botService.SendAsync(e, reply);
                await SendUserNotice(requestUserId, $"你的昵称修改申请已通过～");
                return true;
            }

            if (e.Detail.StartsWith("拒绝昵称"))
            {
                if (!await CheckUser(e, user, false))
                    return true;

                string[] parts = e.Detail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !long.TryParse(parts[1], out long requestId))
                {
                    await botService.SendAsync(e, "格式错误，请使用：`拒绝昵称 <申请ID> [拒绝原因]`");
                    return true;
                }

                string reason = parts.Length >= 3 ? string.Join(" ", parts.Skip(2)) : "";
                bool success = userService.RejectNicknameChange(requestId, out long requestUserId, uid, reason);
                string msg = success ? $"❌ 已拒绝申请 ID {requestId}" : "操作失败，请检查申请ID。";
                if (success && !string.IsNullOrEmpty(reason))
                {
                    msg += $"\n拒绝原因：{reason}";
                }
                md.Content = msg;
                reply.Keyboard = new KeyboardMessage().AppendButtons(1, Button.CreateCmdButton("🔧 审核昵称", "审核昵称"));
                await botService.SendAsync(e, reply);
                await SendUserNotice(requestUserId, $"你的昵称修改申请未通过，拒绝原因：{reason}");
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
