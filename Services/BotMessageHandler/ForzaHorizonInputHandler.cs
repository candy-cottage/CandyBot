using System.Text;
using CandyCottage.CandyBot.Models;
using Milimoe.FunGame.Core.Entity;

namespace CandyCottage.CandyBot.Services;

public partial class CandyBotService
{
    public async Task<bool> HandleForzaHorizonInput(IBotMessage e, User? user)
    {
        MarkdownMessage md = new() { Content = _busy };
        BotReply reply = new() { Markdown = md };

        if (!functionConfig.ForzaHorizon)
        {
            md.Content = "地平线模块正在维护，请稍后再试。";
            await botService.SendAsync(e, reply);
            return true;
        }

        try
        {
            if (e.Detail == "地平线")
            {
                md.Content = $"🥯{"糖果屋".CreateCmdInput()}🍧提供《极限竞速：地平线6》的相关查询功能哦，欢迎体验～\r\n测试中，不代表最终品质；数据来源于ZGC车队的调校摸索表格，如有疑问请联系QQ：3305106902。";
                reply.Keyboard = new KeyboardMessage()
                    .AppendButtons(3,
                        Button.CreateCmdButton("🏎 查车", "查车", false),
                        Button.CreateCmdButton("🛣️ 查赛道", "查赛道", false),
                        Button.CreateCmdButton("📊 查等级", "查等级", false));
                await botService.SendAsync(e, reply);
                return true;
            }

            if (e.Detail.StartsWith("查车"))
            {
                string detail = e.Detail["查车".Length..].Trim();
                string[] parts = detail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 1)
                {
                    int page = 1;
                    string name = parts[0];
                    if (parts.Length > 1 && int.TryParse(parts[1], out int p)) page = p;

                    (bool result, string msg) = forzaService.GetVehicleInfo(name, page, out int totalPages);
                    if (result) md.Content = $"🏎 查询结果（{page} / {totalPages} 页）\r\n{msg}";
                    else md.Content = msg;

                    reply.Keyboard = new();
                    if (totalPages > 1)
                    {
                        reply.Keyboard.AddPaginationRow($"查车 {name} ", page, totalPages);
                    }
                }
                else
                {
                    md.Content = "格式：查车 <车名>\r\n支持模糊查询。";
                    reply.Keyboard = new();
                }
                List<Button> buttons = [
                    Button.CreateCmdButton("🏎 查车", "查车", false),
                    Button.CreateCmdButton("🛣️ 查赛道", "查赛道", false),
                    Button.CreateCmdButton("📊 查等级", "查等级", false),
                ];
                reply.Keyboard.AppendButtonsWithNewRow(3, buttons);
                await botService.SendAsync(e, reply);
                return true;
            }

            if (e.Detail.StartsWith("查等级"))
            {
                string detail = e.Detail["查等级".Length..].Trim();
                string[] parts = detail.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                string? classInput = null;
                string? matchTypeInput = null;
                bool selectOr = true;

                int page = 1;
                if (parts.Length == 1)
                {
                    classInput = parts[0];
                    matchTypeInput = parts[0];
                }
                else if (parts.Length > 1)
                {
                    classInput = parts[0];
                    if (int.TryParse(parts[1], out int p))
                    {
                        page = p;
                        selectOr = true;
                    }
                    else
                    {
                        matchTypeInput = parts[1];
                        if (parts.Length > 2 && int.TryParse(parts[2], out p))
                        {
                            page = p;
                        }
                        selectOr = false;
                    }
                }

                if (classInput == null && matchTypeInput == null)
                {
                    md.Content = "格式：查等级 <[等级]|[比赛类型]/[等级] [比赛类型]>\r\n" +
                                 "示例：查等级 S1 公路\r\n" +
                                 "示例：查等级 A\r\n" +
                                 "示例：查等级 越野\r\n";
                    reply.Keyboard = new KeyboardMessage()
                        .AppendButtons(3,
                            Button.CreateCmdButton("🏎 查车", "查车", false),
                            Button.CreateCmdButton("🛣️ 查赛道", "查赛道", false),
                            Button.CreateCmdButton("📊 查等级", "查等级", false));
                    await botService.SendAsync(e, reply);
                    return true;
                }

                (bool result, string msg) = selectOr ? forzaService.GetVehiclesByClassOrMatchType(classInput, matchTypeInput, page, out int totalPages) : forzaService.GetVehiclesByClassAndMatchType(classInput, matchTypeInput, page, out totalPages);
                if (result) md.Content = $"🏎 查询结果（{page} / {totalPages} 页）\r\n{msg}";
                else md.Content = msg;

                // 构建分页指令基础字符串
                string baseCmd = selectOr ? $"查等级 {classInput ?? ""}".Trim() : $"查等级 {classInput ?? ""} {matchTypeInput ?? ""}".Trim();
                reply.Keyboard = new KeyboardMessage();
                if (totalPages > 1)
                {
                    reply.Keyboard.AddPaginationRow(baseCmd + " ", page, totalPages);
                }
                reply.Keyboard.AppendButtonsWithNewRow(3,
                    Button.CreateCmdButton("🏎 查车", "查车", false),
                    Button.CreateCmdButton("🛣️ 查赛道", "查赛道", false),
                    Button.CreateCmdButton("📊 查等级", "查等级", false));

                await botService.SendAsync(e, reply);
                return true;
            }

            if (e.Detail.StartsWith("修改车辆"))
            {
                if (!await CheckUser(e, user, false))
                {
                    return true;
                }

                string detail = e.Detail["修改车辆".Length..].Trim();
                string[] parts = detail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 1)
                {
                    await botService.SendAsync(e,
                        "格式：修改车辆 <车辆ID> [参数=值]\r\n" +
                        "可用参数：等级= 比赛类型= 制造商= 型号= 年份= 传动= 属性= 胎面= 适用干/湿= 强度= 调校= 备注= 圈速=\r\n" +
                        "示例：修改车辆 123 制造商=法拉利 型号=F40 年份=1992 等级=S2-900 强度=META+ 调校=\"Nyasmowisher[be my light]\"");
                    return true;
                }

                VehicleRequest request = new();

                if (int.TryParse(parts[0], out int vid))
                {
                    request.Id = vid;
                    // 剩余部分解析参数
                    string remaining = string.Join(" ", parts[1..]);
                    if (!ParseKeyValuePairs(remaining, request, out string parseError))
                    {
                        await botService.SendAsync(e, parseError);
                        return true;
                    }
                }
                else
                {
                    md.Content = "请提供车辆 ID。";
                    reply.Keyboard = new KeyboardMessage().AppendButtons(2, Button.CreateCmdButton("🖌 重新修改", e.Detail, false));
                    return true;
                }

                if (!forzaService.UpdateVehicle(request, out string error))
                {
                    md.Content = error;
                    reply.Keyboard = new KeyboardMessage().AppendButtons(2, Button.CreateCmdButton("🖌 重新修改", e.Detail, false));
                    await botService.SendAsync(e, reply);
                    return true;
                }

                md.Content = "车辆信息修改成功。";
                reply.Keyboard = new KeyboardMessage()
                    .AppendButtons(3,
                        Button.CreateCmdButton("🏎 查车", "查车", false),
                        Button.CreateCmdButton("🛣️ 查赛道", "查赛道", false),
                        Button.CreateCmdButton("📊 查等级", "查等级", false));
                await botService.SendAsync(e, reply);
                return true;
            }

            if (e.Detail.StartsWith("新增车辆"))
            {
                if (!await CheckUser(e, user, false))
                {
                    return true;
                }

                string detail = e.Detail["新增车辆".Length..].Trim();
                if (string.IsNullOrEmpty(detail))
                {
                    await botService.SendAsync(e,
                        "格式：新增车辆 制造商=xxx 型号=xxx 等级=xxx 比赛类型=xxx 驱动类型=xxx [其他参数]\r\n" +
                        "必填字段：制造商、型号、等级、比赛类型、驱动类型\r\n" +
                        "可用参数：传动= 属性= 胎面= 适用干/湿= 强度= 调校= 备注= 圈速=\r\n" +
                        "示例：新增车辆 制造商=法拉利 型号=F40 年份=1992 等级=S1 比赛类型=公路 驱动类型=四驱 强度=META 调校=\"Nyasmowisher[be my light]\"\r\n" +
                        "等级：S2, S1, A, B, C, R 或完整格式 S2-900 等\r\n" +
                        "比赛类型：公路、拉力、泥地、越野、街头等");
                    return true;
                }

                VehicleRequest request = new();
                if (!ParseKeyValuePairs(detail, request, out string parseError))
                {
                    await botService.SendAsync(e, parseError);
                    return true;
                }

                // 检查必填字段
                if (string.IsNullOrEmpty(request.Manufacturer) ||
                    string.IsNullOrEmpty(request.Model) ||
                    string.IsNullOrEmpty(request.Class) ||
                    string.IsNullOrEmpty(request.MatchType) ||
                    string.IsNullOrEmpty(request.Drivetrain))
                {
                    md.Content = "缺少必填字段：制造商、型号、等级、比赛类型、驱动类型都必须提供。";
                    reply.Keyboard = new KeyboardMessage().AppendButtons(1, Button.CreateCmdButton("🖌 重新修改", e.Detail, false));
                    await botService.SendAsync(e, reply);
                    return true;
                }

                reply.Keyboard = new KeyboardMessage();
                if (forzaService.AddVehicle(request, out string error))
                {
                    md.Content = "✅ 车辆新增成功！";
                }
                else
                {
                    md.Content = $"❌ 新增失败：{error}";
                    if (md.Content.Contains("请使用“修改车辆”指令"))
                    {
                        reply.Keyboard.AppendButtons(2, Button.CreateCmdButton("🔁 修改车辆", e.Detail.Replace("新增车辆", "修改车辆"), false));
                    }
                }

                reply.Keyboard.AppendButtons(3,
                    Button.CreateCmdButton("🏎 查车", "查车", false),
                    Button.CreateCmdButton("🛣️ 查赛道", "查赛道", false),
                    Button.CreateCmdButton("📊 查等级", "查等级", false));
                await botService.SendAsync(e, reply);
                return true;
            }

            if (e.Detail.StartsWith("删除车辆"))
            {
                if (!await CheckUser(e, user, false))
                    return true;

                string detail = e.Detail["删除车辆".Length..].Trim();
                if (string.IsNullOrEmpty(detail))
                {
                    await botService.SendAsync(e,
                        "格式：\n" +
                        "1. 按 ID 删除：删除车辆 <id> [id2 id3 ...]\n" +
                        "   示例：删除车辆 123\n" +
                        "   示例：删除车辆 1 2 3\n" +
                        "2. 按字段删除：删除车辆 制造商=xxx 型号=xxx 年份=yyyy 等级=xxx 比赛类型=xxx\n" +
                        "   示例：删除车辆 制造商=法拉利 型号=F40 年份=1992 等级=S1 比赛类型=公路\n" +
                        "   ⚠️ 按字段删除会列出所有匹配的车辆并需要二次确认。");
                    return true;
                }

                // 尝试解析为ID列表（空格或逗号分隔）
                string[] parts = detail.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
                List<int> ids = [];
                bool allAreNumbers = true;
                foreach (string p in parts)
                {
                    if (int.TryParse(p, out int id))
                        ids.Add(id);
                    else
                    {
                        allAreNumbers = false;
                        break;
                    }
                }

                // 情况1：纯数字，按ID批量删除（直接执行，无需二次确认）
                if (allAreNumbers && ids.Count > 0)
                {
                    var (success, msg, deletedCount) = forzaService.DeleteVehiclesByIds(ids);
                    md.Content = success ? $"✅ {msg}" : $"❌ {msg}";
                    reply.Keyboard = new KeyboardMessage()
                        .AppendButtons(3,
                            Button.CreateCmdButton("🏎 查车", "查车", false),
                            Button.CreateCmdButton("🛣️ 查赛道", "查赛道", false),
                            Button.CreateCmdButton("📊 查等级", "查等级", false));
                    await botService.SendAsync(e, reply);
                    return true;
                }

                // 情况2：非纯数字，按字段匹配删除（需要二次确认）
                VehicleRequest request = new();
                if (!ParseKeyValuePairs(detail, request, out string parseError))
                {
                    await botService.SendAsync(e, parseError);
                    return true;
                }

                var (findSuccess, findMsg, matches) = forzaService.FindVehiclesByCriteria(request);
                if (!findSuccess || matches == null || matches.Count == 0)
                {
                    md.Content = findMsg;
                    await botService.SendAsync(e, reply);
                    return true;
                }

                // 构建显示列表
                StringBuilder sb = new();
                sb.AppendLine($"⚠️ 找到 {matches.Count} 辆匹配的车辆：");
                foreach (ForzaService.VehicleMatch m in matches)
                {
                    sb.AppendLine($"• ID {m.Id}：{m.DisplayName}");
                }
                sb.AppendLine("\n是否确认删除以上所有车辆？");

                md.Content = sb.ToString();
                reply.Keyboard = new KeyboardMessage()
                    .AppendButtons(1, Button.CreateCmdButton("✅ 确认删除", e.Detail, false));  // 携带原始参数
                await botService.SendAsync(e, reply);
                return true;
            }

            if (e.Detail.StartsWith("确认删除车辆"))
            {
                if (!await CheckUser(e, user, false))
                    return true;

                string detail = e.Detail["确认删除车辆".Length..].Trim();
                if (string.IsNullOrEmpty(detail))
                {
                    md.Content = "请提供要删除的车辆信息，例如：确认删除车辆 制造商=xxx ...";
                    await botService.SendAsync(e, reply);
                    return true;
                }

                // 重新解析字段
                VehicleRequest request = new();
                if (!ParseKeyValuePairs(detail, request, out string parseError))
                {
                    await botService.SendAsync(e, parseError);
                    return true;
                }

                var (findSuccess, findMsg, matches) = forzaService.FindVehiclesByCriteria(request);
                if (!findSuccess || matches == null || matches.Count == 0)
                {
                    md.Content = findMsg;
                    await botService.SendAsync(e, reply);
                    return true;
                }

                // 批量删除所有匹配的ID
                List<int> idsToDelete = [.. matches.Select(m => m.Id)];
                var (delSuccess, delMsg, _) = forzaService.DeleteVehiclesByIds(idsToDelete);
                if (delSuccess)
                {
                    md.Content = $"✅ 已删除 {matches.Count} 辆车：\n" + string.Join("\n", matches.Select(m => $"• ID {m.Id}：{m.DisplayName}"));
                }
                else
                {
                    md.Content = $"❌ 删除失败：{delMsg}";
                }

                reply.Keyboard = new KeyboardMessage()
                    .AppendButtons(3,
                        Button.CreateCmdButton("🏎 查车", "查车", false),
                        Button.CreateCmdButton("🛣️ 查赛道", "查赛道", false),
                        Button.CreateCmdButton("📊 查等级", "查等级", false));
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

    private static bool ParseKeyValuePairs(string input, VehicleRequest request, out string error)
    {
        error = "";
        System.Text.RegularExpressions.MatchCollection matches = GetParamValue().Matches(input);
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            string key = m.Groups[1].Value.ToLowerInvariant();
            string val = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;

            switch (key)
            {
                case "等级":
                case "class":
                    request.Class = val;
                    break;
                case "比赛类型":
                case "matchtype":
                    request.MatchType = val;
                    break;
                case "制造商":
                case "manufacturer":
                    request.Manufacturer = val;
                    break;
                case "型号":
                case "model":
                    request.Model = val;
                    break;
                case "年份":
                case "year":
                    request.Year = val;
                    break;
                case "传动":
                case "驱动":
                case "drivetrain":
                    request.Drivetrain = val;
                    break;
                case "属性":
                case "attribute":
                    request.Attribute = val;
                    break;
                case "胎面":
                case "tirecompound":
                    request.TireCompound = val;
                    break;
                case "适用干/湿":
                case "surfacecondition":
                    request.SurfaceCondition = val;
                    break;
                case "强度":
                case "strength":
                    request.Strength = val;
                    break;
                case "调校":
                case "tune":
                    request.Tune = val;
                    break;
                case "备注":
                case "remarks":
                    request.Remarks = val;
                    break;
                case "圈速":
                case "laptime":
                    request.LapTime = val;
                    break;
                default:
                    error = $"未知参数：{key}";
                    return false;
            }
        }
        return true;
    }
}
