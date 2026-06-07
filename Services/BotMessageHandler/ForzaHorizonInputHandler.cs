using System.Data;
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

            if (e.Detail.StartsWith("同步车辆表"))
            {
                if (!await CheckUser(e, user))
                {
                    return true;
                }

                string fileId = e.Detail["同步车辆表".Length..].Trim();
                if (fileId == "" && forzaConfig.DocsID.TryGetValue("ZGC", out string? value) && value != null)
                {
                    fileId = value;
                }

                if (fileId == "")
                {
                    md.Content = "请提供要同步的表格ID，例如：\n同步车辆表 <表格ID>\n如果不提供ID，将默认使用配置中的ZGC车队表格。若仍然显示该提示，请检查配置文件。";
                    await botService.SendAsync(e, reply);
                    return true;
                }

                ForzaSheetSyncReport report = await forzaSheetImporter.SyncAsync(fileId);
                if (report.Success)
                {
                    md.Content =
                        $"**文件：{fileId}**\r\n" +
                        $"✅ 已同步 {report.InsertedVehicleCount} 辆车辆。\r\n" +
                        $"原表：{report.PreviousVehicleCount} 辆；解析：{report.ParsedVehicleCount} 辆；工作表：{report.ParsedSheetNames.Count} 张。\r\n" +
                        $"跳过工作表：{report.SkippedSheetNames.Count} 张；跳过行：{report.SkippedRows.Count} 行。" +
                        (report.Diagnostics.Count > 0 ? "\r\n诊断：" + string.Join("\r\n", report.Diagnostics.Take(3)) : "");
                }
                else
                {
                    md.Content =
                        $"**文件：{fileId}**\r\n" +
                        $"❌ 同步车辆表失败：{report.ErrorMessage}\r\n" +
                        $"旧数据已保留。当前原表记录：{report.PreviousVehicleCount} 辆；解析：{report.ParsedVehicleCount} 辆。";
                }

                await sql.ExecuteDataSetAsync("Select Distinct Manufacturer From Vehicles");
                if (sql.Success)
                {
                    string[] manufacturers = [.. sql.DataSet.Tables[0].AsEnumerable().Select(r => r["Manufacturer"].ToString() ?? "")];
                    forzaConfig.Update(manufacturers);
                }

                await botService.SendAsync(e, reply);
                return true;
            }

            if (e.Detail.StartsWith("查赛道"))
            {
                string detail = e.Detail["查赛道".Length..].Trim();
                List<Button> buttons = [
                    Button.CreateCmdButton("🏎 查车", "查车", false),
                    Button.CreateCmdButton("🛣️ 查赛道", "查赛道", false),
                    Button.CreateCmdButton("📊 查等级", "查等级", false),
                ];
                reply.Keyboard = new KeyboardMessage();

                // 分离页码
                string[] tokens = detail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                int page = 1;
                string queryStr = detail;
                if (tokens.Length > 0 && int.TryParse(tokens[^1], out int lastNum))
                {
                    page = lastNum;
                    queryStr = string.Join(" ", tokens.Take(tokens.Length - 1)).Trim();
                }

                if (string.IsNullOrEmpty(queryStr))
                {
                    md.Content = "格式：查赛道 <关键字> [页码]\r\n" +
                                 "示例：查赛道 海滨\r\n" +
                                 "示例：查赛道 volcano\r\n" +
                                 "支持按中文名、英文名、别名搜索。";
                    reply.Keyboard.AppendButtonsWithNewRow(3, buttons);
                    await botService.SendAsync(e, reply);
                    return true;
                }

                var (success, msg, data, totalPages) = forzaService.SearchMaps(queryStr, page);
                if (success && data != null)
                {
                    string cards = ForzaService.RenderMapsAsCards(data);
                    md.Content = $"🛣️ 查询结果（{page} / {totalPages} 页）\r\n{cards}";
                    if (totalPages > 1)
                        reply.Keyboard.AddPaginationRow($"查赛道 {queryStr} ", page, totalPages);
                }
                else
                {
                    md.Content = msg;
                }
                reply.Keyboard.AppendButtonsWithNewRow(3, buttons);
                await botService.SendAsync(e, reply);
                return true;
            }

            if (e.Detail.StartsWith("查车"))
            {
                string detail = e.Detail["查车".Length..].Trim();
                List<Button> buttons = [
                    Button.CreateCmdButton("🏎 查车", "查车", false),
                    Button.CreateCmdButton("🛣️ 查赛道", "查赛道", false),
                    Button.CreateCmdButton("📊 查等级", "查等级", false),
                ];
                reply.Keyboard = new KeyboardMessage();

                // 分离页码（最后一个 token 是数字）
                string[] tokens = detail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                int page = 1;
                string queryStr = detail;
                if (tokens.Length > 0 && int.TryParse(tokens[^1], out int lastNum))
                {
                    page = lastNum;
                    queryStr = string.Join(" ", tokens.Take(tokens.Length - 1)).Trim();
                }

                if (string.IsNullOrEmpty(queryStr))
                {
                    md.Content = "格式：查车 <制造商/型号> [页码]\r\n" +
                        "示例：查车 法拉利\r\n" +
                        "示例：查车 FXX\r\n" +
                        "示例：查车 法拉利FXX\r\n" +
                        "支持模糊查询，如：查车 FX。";
                    reply.Keyboard.AppendButtonsWithNewRow(3, buttons);
                    await botService.SendAsync(e, reply);
                    return true;
                }

                var (manufacturer, model) = ParseManufacturerModel(queryStr);
                // 如果没有匹配到制造商，则将整个字符串作为型号查询
                bool useOr = manufacturer == null;
                if (useOr)
                {
                    manufacturer = queryStr;
                    model = queryStr;
                }

                // 调用服务查询
                (bool result, string msg) = forzaService.GetVehicleInfo(manufacturer, model, useOr, page, out int totalPages);
                if (result) md.Content = $"🏎 查询结果（{page} / {totalPages} 页）\r\n{msg}";
                else md.Content = msg;
                if (totalPages > 1)
                {
                    reply.Keyboard.AddPaginationRow($"查车 {queryStr} ", page, totalPages);
                }
                reply.Keyboard.AppendButtonsWithNewRow(3, buttons);
                await botService.SendAsync(e, reply);
                return true;
            }

            if (e.Detail.StartsWith("查等级"))
            {
                string detail = e.Detail["查等级".Length..].Trim();
                if (detail == "")
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

                // ----- 智能解析参数 -----
                string[] tokens = detail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                int page = 1;
                string paramStr = detail;

                // 如果最后一个 token 是数字，则视为页码
                if (tokens.Length > 0 && int.TryParse(tokens[^1], out int lastNum))
                {
                    page = lastNum;
                    paramStr = string.Join(" ", tokens.Take(tokens.Length - 1));
                }

                string? classInput = null;
                string? matchTypeInput = null;
                bool useAndCondition = false; // true: AND, false: OR

                if (paramStr.Contains(' '))
                {
                    // 带空格：按顺序取第一个词为等级，第二个词为比赛类型（允许只填一个）
                    string[] parts = paramStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    classInput = parts[0];
                    if (parts.Length > 1)
                        matchTypeInput = parts[1];
                    useAndCondition = matchTypeInput != null;
                }
                else
                {
                    // 无空格：尝试拆分为「等级+比赛类型」或纯等级
                    (string? cls, string? match) = SplitClassAndMatchType(paramStr);
                    if (cls != null)
                    {
                        // 成功匹配到等级模式（如 S1、A、S2-900 等）
                        classInput = cls;
                        matchTypeInput = match;
                        useAndCondition = matchTypeInput != null;
                    }
                    else
                    {
                        // 无法匹配等级 → 将整个输入视为比赛类型
                        classInput = null;
                        matchTypeInput = paramStr;
                        useAndCondition = false;
                    }
                }

                // 执行查询
                (bool result, string msg) = useAndCondition ? forzaService.GetVehiclesByClassAndMatchType(classInput, matchTypeInput, page, out int totalPages) : forzaService.GetVehiclesByClassOrMatchType(classInput, matchTypeInput, page, out totalPages);

                if (result) md.Content = $"🏎 查询结果（{page} / {totalPages} 页）\r\n{msg}";
                else md.Content = $"{msg.Trim()}\r\n{md.Content}";

                // 构建分页指令基础字符串
                string baseCmd = !useAndCondition ? $"查等级 {classInput ?? ""}".Trim() : $"查等级 {classInput ?? ""} {matchTypeInput ?? ""}".Trim();
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

            // ========== 赛道管理（管理员） ==========

            if (e.Detail.StartsWith("新增赛道"))
            {
                if (!await CheckUser(e, user, false))
                    return true;

                string detail = e.Detail["新增赛道".Length..].Trim();
                if (string.IsNullOrEmpty(detail))
                {
                    await botService.SendAsync(e,
                        "格式：新增赛道 中文名=xxx 英文名=xxx 路线类型=xxx 长度=xxx [其他参数]\r\n" +
                        "必填字段：中文名、路线类型（circuit/sprint）\r\n" +
                        "可用参数：中文名= 英文名= 别名= 长度(km)= 路线类型= 路面= 赛事大类= 赛道特性= 难度= 海拔= 弯道数= 路宽= 圈速(秒)= 世界纪录= WR车辆= WR调校= 推荐等级= 推荐车型= 区域= 最佳赛季= 天气= 夜间= DLC= 反向= 图片URL= 小地图URL= 备注=\r\n" +
                        "路线类型：circuit=圈图, sprint=冲刺图\r\n" +
                        "路面：asphalt/dirt/gravel/sand/mixed/snow/cross_country\r\n" +
                        "赛事大类：road/dirt/cross_country/street/drag/rally/showcase\r\n" +
                        "赛道特性：handling/speed/balanced/offroad/drift/technical\r\n" +
                        "示例：新增赛道 中文名=海滨冲刺 英文名=Coastal Sprint 路线类型=sprint 长度=8.4 路面=asphalt 赛道特性=speed 难度=2 区域=Pacific Coast");
                    return true;
                }

                MapRequest mapRequest = new();
                if (!ParseMapKeyValuePairs(detail, mapRequest, out string parseError))
                {
                    await botService.SendAsync(e, parseError);
                    return true;
                }

                if (string.IsNullOrEmpty(mapRequest.NameZh) || string.IsNullOrEmpty(mapRequest.RouteType))
                {
                    md.Content = "缺少必填字段：中文名、路线类型都必须提供。";
                    reply.Keyboard = new KeyboardMessage().AppendButtons(1, Button.CreateCmdButton("🖌 重新修改", e.Detail, false));
                    await botService.SendAsync(e, reply);
                    return true;
                }

                reply.Keyboard = new KeyboardMessage();
                if (forzaService.AddMap(mapRequest, out string addError))
                {
                    md.Content = "✅ 赛道新增成功！";
                }
                else
                {
                    md.Content = $"❌ 新增失败：{addError}";
                }

                reply.Keyboard.AppendButtons(3,
                    Button.CreateCmdButton("🛣️ 查赛道", "查赛道", false),
                    Button.CreateCmdButton("🏎 查车", "查车", false),
                    Button.CreateCmdButton("📊 查等级", "查等级", false));
                await botService.SendAsync(e, reply);
                return true;
            }

            if (e.Detail.StartsWith("修改赛道"))
            {
                if (!await CheckUser(e, user, false))
                    return true;

                string detail = e.Detail["修改赛道".Length..].Trim();
                string[] parts = detail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 1)
                {
                    await botService.SendAsync(e,
                        "格式：修改赛道 <赛道ID> [参数=值]\r\n" +
                        "可用参数：中文名= 英文名= 别名= 长度(km)= 路线类型= 路面= 赛事大类= 赛道特性= 难度= 海拔= 弯道数= 路宽= 圈速(秒)= 世界纪录= WR车辆= WR调校= 推荐等级= 推荐车型= 区域= 最佳赛季= 天气= 夜间= DLC= 反向= 图片URL= 小地图URL= 备注=\r\n" +
                        "示例：修改赛道 1 难度=4 赛道特性=technical");
                    return true;
                }

                MapRequest mapRequest = new();
                if (int.TryParse(parts[0], out int mid))
                {
                    mapRequest.Id = mid;
                    string remaining = string.Join(" ", parts[1..]);
                    if (!ParseMapKeyValuePairs(remaining, mapRequest, out string parseError))
                    {
                        await botService.SendAsync(e, parseError);
                        return true;
                    }
                }
                else
                {
                    md.Content = "请提供赛道 ID。";
                    reply.Keyboard = new KeyboardMessage().AppendButtons(2, Button.CreateCmdButton("🖌 重新修改", e.Detail, false));
                    await botService.SendAsync(e, reply);
                    return true;
                }

                if (!forzaService.UpdateMap(mapRequest, out string updateError))
                {
                    md.Content = updateError;
                    reply.Keyboard = new KeyboardMessage().AppendButtons(2, Button.CreateCmdButton("🖌 重新修改", e.Detail, false));
                    await botService.SendAsync(e, reply);
                    return true;
                }

                md.Content = "赛道信息修改成功。";
                reply.Keyboard = new KeyboardMessage()
                    .AppendButtons(3,
                        Button.CreateCmdButton("🛣️ 查赛道", "查赛道", false),
                        Button.CreateCmdButton("🏎 查车", "查车", false),
                        Button.CreateCmdButton("📊 查等级", "查等级", false));
                await botService.SendAsync(e, reply);
                return true;
            }

            if (e.Detail.StartsWith("删除赛道"))
            {
                if (!await CheckUser(e, user, false))
                    return true;

                string detail = e.Detail["删除赛道".Length..].Trim();
                if (string.IsNullOrEmpty(detail))
                {
                    await botService.SendAsync(e,
                        "格式：\n" +
                        "1. 按 ID 删除：删除赛道 <id> [id2 id3 ...]\n" +
                        "   示例：删除赛道 1\n" +
                        "   示例：删除赛道 1 2 3\n" +
                        "2. 按字段删除：删除赛道 中文名=xxx 路线类型=xxx 区域=xxx\n" +
                        "   示例：删除赛道 中文名=海滨冲刺 路线类型=sprint\n" +
                        "   ⚠️ 按字段删除会列出所有匹配的赛道并需要二次确认。");
                    return true;
                }

                string[] delParts = detail.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
                List<int> ids = [];
                bool allAreNumbers = true;
                foreach (string p in delParts)
                {
                    if (int.TryParse(p, out int id))
                        ids.Add(id);
                    else
                    {
                        allAreNumbers = false;
                        break;
                    }
                }

                if (allAreNumbers && ids.Count > 0)
                {
                    var (delSuccess, delMsg, _) = forzaService.DeleteMapsByIds(ids);
                    md.Content = delSuccess ? $"✅ {delMsg}" : $"❌ {delMsg}";
                    reply.Keyboard = new KeyboardMessage()
                        .AppendButtons(3,
                            Button.CreateCmdButton("🛣️ 查赛道", "查赛道", false),
                            Button.CreateCmdButton("🏎 查车", "查车", false),
                            Button.CreateCmdButton("📊 查等级", "查等级", false));
                    await botService.SendAsync(e, reply);
                    return true;
                }

                MapRequest mapDelRequest = new();
                if (!ParseMapKeyValuePairs(detail, mapDelRequest, out string parseError2))
                {
                    await botService.SendAsync(e, parseError2);
                    return true;
                }

                var (findSuccess, findMsg, matches) = forzaService.FindMapsByCriteria(mapDelRequest);
                if (!findSuccess || matches == null || matches.Count == 0)
                {
                    md.Content = findMsg;
                    await botService.SendAsync(e, reply);
                    return true;
                }

                StringBuilder sb = new();
                sb.AppendLine($"⚠️ 找到 {matches.Count} 条匹配的赛道：");
                foreach (ForzaService.MapMatch m in matches)
                    sb.AppendLine($"• ID {m.Id}：{m.DisplayName}");
                sb.AppendLine("\n是否确认删除以上所有赛道？");

                md.Content = sb.ToString();
                reply.Keyboard = new KeyboardMessage()
                    .AppendButtons(1, Button.CreateCmdButton("✅ 确认删除赛道", detail, false));
                await botService.SendAsync(e, reply);
                return true;
            }

            if (e.Detail.StartsWith("确认删除赛道"))
            {
                if (!await CheckUser(e, user, false))
                    return true;

                string detail = e.Detail["确认删除赛道".Length..].Trim();
                if (string.IsNullOrEmpty(detail))
                {
                    md.Content = "请提供要删除的赛道信息，例如：确认删除赛道 中文名=xxx ...";
                    await botService.SendAsync(e, reply);
                    return true;
                }

                MapRequest mapConfirmRequest = new();
                if (!ParseMapKeyValuePairs(detail, mapConfirmRequest, out string parseError3))
                {
                    await botService.SendAsync(e, parseError3);
                    return true;
                }

                var (findSuccess, findMsg, matches) = forzaService.FindMapsByCriteria(mapConfirmRequest);
                if (!findSuccess || matches == null || matches.Count == 0)
                {
                    md.Content = findMsg;
                    await botService.SendAsync(e, reply);
                    return true;
                }

                List<int> idsToDelete = [.. matches.Select(m => m.Id)];
                var (delSuccess, delMsg, _) = forzaService.DeleteMapsByIds(idsToDelete);
                if (delSuccess)
                {
                    md.Content = $"✅ 已删除 {matches.Count} 条赛道：\n" + string.Join("\n", matches.Select(m => $"• ID {m.Id}：{m.DisplayName}"));
                }
                else
                {
                    md.Content = $"❌ 删除失败：{delMsg}";
                }

                reply.Keyboard = new KeyboardMessage()
                    .AppendButtons(3,
                        Button.CreateCmdButton("🛣️ 查赛道", "查赛道", false),
                        Button.CreateCmdButton("🏎 查车", "查车", false),
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

    private static (string? classPart, string? matchTypePart) SplitClassAndMatchType(string input)
    {
        if (string.IsNullOrEmpty(input))
            return (null, null);

        // 匹配以字母开头，后跟零个或多个数字的等级模式（如 S1, A, B, S2）
        System.Text.RegularExpressions.Match match = MatchClassAndMatchType().Match(input);
        if (match.Success)
        {
            string classPart = match.Groups[1].Value;
            string rest = match.Groups[2].Value;
            if (string.IsNullOrEmpty(rest))
                return (classPart, null);
            else
                return (classPart, rest);
        }
        return (null, null);
    }

    private (string? manufacturer, string? model) ParseManufacturerModel(string input)
    {
        IReadOnlyList<string> manufacturers = forzaConfig.Manufacturers;

        // 优先尝试最长匹配（按长度降序排序）
        List<string> sorted = [.. manufacturers.OrderByDescending(m => m.Length)];
        string? matchedManufacturer = null;
        string? remaining = null;

        foreach (string m in sorted)
        {
            // 忽略大小写，且要求制造商关键词必须作为子串出现在输入中
            if (input.Contains(m, StringComparison.OrdinalIgnoreCase))
            {
                matchedManufacturer = m;

                // 移除制造商部分，得到剩余字符串（注意处理大小写）
                int index = input.IndexOf(m, StringComparison.OrdinalIgnoreCase);
                remaining = input.Remove(index, m.Length).Trim();

                // 如果剩余部分为空，则型号为 null
                if (string.IsNullOrEmpty(remaining))
                {
                    remaining = null;
                }
                break;
            }
        }

        return (matchedManufacturer, remaining);
    }

    private static bool ParseMapKeyValuePairs(string input, MapRequest request, out string error)
    {
        error = "";
        System.Text.RegularExpressions.MatchCollection matches = GetParamValue().Matches(input);
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            string key = m.Groups[1].Value.ToLowerInvariant();
            string val = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;

            switch (key)
            {
                case "中文名":
                case "namezh":
                    request.NameZh = val;
                    break;
                case "英文名":
                case "nameen":
                    request.NameEn = val;
                    break;
                case "别名":
                case "aliases":
                    request.Aliases = val;
                    break;
                case "长度":
                case "长度(km)":
                case "lengthkm":
                    request.LengthKm = val;
                    break;
                case "路线类型":
                case "routetype":
                    request.RouteType = val;
                    break;
                case "路面":
                case "surface":
                    request.Surface = val;
                    break;
                case "赛事大类":
                case "racecategory":
                    request.RaceCategory = val;
                    break;
                case "赛道特性":
                case "characteristic":
                    request.Characteristic = val;
                    break;
                case "难度":
                case "difficulty":
                    request.Difficulty = val;
                    break;
                case "海拔":
                case "elevation":
                    request.Elevation = val;
                    break;
                case "弯道数":
                case "弯道":
                case "turncount":
                    request.TurnCount = val;
                    break;
                case "路宽":
                case "roadwidth":
                    request.RoadWidth = val;
                    break;
                case "圈速":
                case "圈速(秒)":
                case "avgtimesec":
                    request.AvgTimeSec = val;
                    break;
                case "世界纪录":
                case "worldrecord":
                    request.WorldRecord = val;
                    break;
                case "wr车辆":
                case "wrvehicle":
                    request.WrVehicle = val;
                    break;
                case "wr调校":
                case "wrtuning":
                    request.WrTuning = val;
                    break;
                case "推荐等级":
                case "carclassbest":
                    request.CarClassBest = val;
                    break;
                case "推荐车型":
                case "cartypebest":
                    request.CarTypeBest = val;
                    break;
                case "区域":
                case "region":
                    request.Region = val;
                    break;
                case "最佳赛季":
                case "seasonbest":
                    request.SeasonBest = val;
                    break;
                case "天气":
                case "weatherdefault":
                    request.WeatherDefault = val;
                    break;
                case "夜间":
                case "nightmode":
                    request.NightMode = val;
                    break;
                case "dlc":
                    request.Dlc = val;
                    break;
                case "反向":
                case "isreverse":
                    request.IsReverse = val;
                    break;
                case "图片url":
                case "imageurl":
                    request.ImageUrl = val;
                    break;
                case "小地图url":
                case "minimapurl":
                    request.MinimapUrl = val;
                    break;
                case "备注":
                case "notes":
                    request.Notes = val;
                    break;
                default:
                    error = $"未知参数：{key}";
                    return false;
            }
        }
        return true;
    }
}
