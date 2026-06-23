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
                md.Content = $"🥯 {"糖果屋".CreateCmdInput()} 🍧提供《极限竞速：地平线6》的相关查询功能哦，欢迎体验～\r\n测试中，不代表最终品质，如有疑问请联系维护者QQ：3305106902。\r\n**鸣谢**\r\n开发者：Mili's Candy Cottage\r\ngithub@milimoe、github@iHaydenzZ\r\n车辆调校数据来源：ZGC俱乐部《地平线6车辆摸索》\r\n赛道数据来源：ThickSea214561《FH6赛道名称中英文对照表》";
                reply.Keyboard = new KeyboardMessage().AppendButtons(2, ForzaNavButtons());
                await botService.SendAsync(e, reply);
                return true;
            }

            if (e.Detail.StartsWith("同步车辆表"))
            {
                if (!await CheckUser(e, user))
                {
                    return true;
                }

                // 指令已改为统一同步配置（DocsID）中的所有表格，不再支持指定表格ID——
                // 静默忽略参数会让管理员误以为同步了指定表格，必须显式拒绝
                string extraArg = e.Detail["同步车辆表".Length..].Trim();
                if (extraArg != "")
                {
                    await botService.SendAsync(e, "“同步车辆表”已不支持指定表格ID，将统一同步配置（DocsID）中的所有表格。\r\n如需同步新表格，请先将其加入配置，再直接发送：同步车辆表");
                    return true;
                }

                // 捕获触发者信息（后台任务中 DI scope 已关闭，通过闭包传入）
                long actorUserId = user?.Id ?? 0;
                string actorOpenId = e.AuthorOpenId;
                List<(string Label, string FileId)> targets = [.. forzaConfig.DocsID.Select(kv => (kv.Key, kv.Value))];

                // 入后台队列：立即回复"已提交"，不阻塞消息处理线程
                await taskQueue.QueueAsync(async _ =>
                {
                    foreach ((string label, string fileId) in targets)
                    {
                        try
                        {
                            ForzaSheetSyncReport report = await forzaSheetImporter.SyncAsync(fileId, actorUserId);
                            string summary = report.Success
                                ? $"[{label}] 同步完成：新增 {report.InsertedTuneCount} 条调校，解析 {report.ParsedTuneCount} 行。"
                                : $"[{label}] 同步失败：{report.ErrorMessage}";
                            // 直接调用 SendActiveMessageAsync（此处已在队列任务中，不能再入队）
                            await botService.SendActiveMessageAsync(actorOpenId, false, (BotReply)summary);
                        }
                        catch (Exception ex)
                        {
                            if (logger.IsEnabled(LogLevel.Error))
                                logger.LogError(ex, "手动同步车辆表异常：{FileId}", fileId);
                            await botService.SendActiveMessageAsync(actorOpenId, false, (BotReply)$"[{label}] 同步异常：{ex.Message}");
                        }
                    }
                });

                await botService.SendAsync(e, "同步已提交，完成后会通知你结果。");
                return true;
            }

            if (e.Detail.StartsWith("刷新制造商缓存"))
            {
                if (!await CheckUser(e, user))
                {
                    return true;
                }

                await sql.ExecuteDataSetAsync("Select Distinct Manufacturer From Vehicles");
                if (sql.Success)
                {
                    string[] manufacturers = [.. sql.DataSet.Tables[0].AsEnumerable().Select(r => r["Manufacturer"].ToString() ?? "")];
                    forzaConfig.Invalidate();
                    forzaConfig.Update(manufacturers);
                    await botService.SendAsync(e, $"刷新完成，当前缓存 {manufacturers.Length} 个制造商。");
                }
                else
                {
                    await botService.SendAsync(e, "刷新失败：查询制造商列表失败，请稍后再试。");
                }
                return true;
            }

            if (e.Detail.StartsWith("查赛道"))
            {
                string detail = e.Detail["查赛道".Length..].Trim();
                reply.Keyboard = new KeyboardMessage();

                // 分离页码
                (string queryStr, int page) = SplitPageParam(detail);

                if (string.IsNullOrEmpty(queryStr))
                {
                    md.Content = "格式：查赛道 <关键字> -p [页码]\r\n" +
                                 "示例：查赛道 白川\r\n" +
                                 "示例：查赛道 Shirakawa\r\n" +
                                 "支持按中文名、英文名、别名搜索。";
                    reply.Keyboard.AppendButtonsWithNewRow(2, ForzaNavButtons());
                    await botService.SendAsync(e, reply);
                    return true;
                }

                var (success, msg, data, totalPages) = forzaService.SearchMaps(queryStr, page);
                if (success && data != null)
                {
                    string cards = ForzaService.RenderMapsAsCards(data);
                    md.Content = $"🛣️ 查询结果（{page} / {totalPages} 页）\r\n{cards}";
                    if (totalPages > 1)
                        reply.Keyboard.AddPaginationRow($"查赛道 {queryStr} -p ", page, totalPages);
                }
                else
                {
                    md.Content = msg;
                }
                reply.Keyboard.AppendButtonsWithNewRow(2, ForzaNavButtons());
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

                // 分离页码
                (string queryStr, int page) = SplitPageParam(detail);

                if (string.IsNullOrEmpty(queryStr))
                {
                    md.Content = "格式：查车 <制造商/型号> -p [页码]\r\n" +
                        "示例：查车 法拉利\r\n" +
                        "示例：查车 FXX\r\n" +
                        "示例：查车 法拉利FXX\r\n" +
                        "支持模糊查询，如：查车 FX。";
                    reply.Keyboard.AppendButtonsWithNewRow(2, ForzaNavButtons());
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
                    reply.Keyboard.AddPaginationRow($"查车 {queryStr} -p ", page, totalPages);
                }
                reply.Keyboard.AppendButtonsWithNewRow(2, ForzaNavButtons());
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
                    reply.Keyboard = new KeyboardMessage().AppendButtons(2, ForzaNavButtons());
                    await botService.SendAsync(e, reply);
                    return true;
                }

                // ----- 智能解析参数 -----
                (string paramStr, int page) = SplitPageParam(detail);

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
                    reply.Keyboard.AddPaginationRow(baseCmd + " -p ", page, totalPages);
                }
                reply.Keyboard.AppendButtonsWithNewRow(2, ForzaNavButtons());

                await botService.SendAsync(e, reply);
                return true;
            }

            // 修改车型：按 VehicleId 更新 Vehicles 行的制造商/型号/年份
            // 必须在 修改车辆 之前匹配，防止前缀被截断
            if (e.Detail.StartsWith("修改车型"))
            {
                if (!await CheckUser(e, user, false))
                    return true;

                string detail = e.Detail["修改车型".Length..].Trim();
                string[] parts = detail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 1 || !int.TryParse(parts[0], out int modelId))
                {
                    await botService.SendAsync(e,
                        "格式：修改车型 <车型ID> 制造商=xxx 型号=xxx [年份=yyyy]\r\n" +
                        "示例：修改车型 5 制造商=法拉利 型号=F40 年份=1992");
                    return true;
                }

                VehicleModelRequest modelReq = new() { Id = modelId };
                string modelRemaining = string.Join(" ", parts[1..]);
                if (!ParseModelKeyValuePairs(modelRemaining, modelReq, out string modelParseErr))
                {
                    await botService.SendAsync(e, modelParseErr);
                    return true;
                }

                if (string.IsNullOrWhiteSpace(modelReq.Manufacturer) || string.IsNullOrWhiteSpace(modelReq.Model))
                {
                    md.Content = "缺少必填字段：制造商、型号都必须提供。";
                    reply.Keyboard = new KeyboardMessage().AppendButtons(1, Button.CreateCmdButton("🖌 重新修改", e.Detail, false));
                    await botService.SendAsync(e, reply);
                    return true;
                }

                if (!forzaService.UpdateVehicleModel(modelReq, user!.Id, out string modelUpdErr))
                {
                    md.Content = $"❌ 修改车型失败：{modelUpdErr}";
                    reply.Keyboard = new KeyboardMessage().AppendButtons(2, Button.CreateCmdButton("🖌 重新修改", e.Detail, false));
                    await botService.SendAsync(e, reply);
                    return true;
                }

                md.Content = "✅ 车型信息修改成功。";
                reply.Keyboard = new KeyboardMessage().AppendButtons(2, ForzaNavButtons());
                await botService.SendAsync(e, reply);
                return true;
            }

            // 修改车辆（兼容别名）→ 实际委托给「修改调校」语义：
            // 查车结果卡片展示的 ID 是 TuneId，管理员看到的是调校行，故「修改车辆 <id>」
            // 语义上等价于「修改调校 <id>」，不再操作 Vehicles 行。
            if (e.Detail.StartsWith("修改车辆"))
            {
                if (!await CheckUser(e, user, false))
                    return true;

                string detail = e.Detail["修改车辆".Length..].Trim();
                string[] parts = detail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 1)
                {
                    await botService.SendAsync(e,
                        "格式：修改车辆 <调校ID> [参数=值]\r\n" +
                        "（调校ID 即查车结果中的 ID，修改调校显示字段）\r\n" +
                        "可用参数：等级= 比赛类型= 驱动= 属性= 胎面= 干湿地= 强度= 调校= 备注= 圈速=\r\n" +
                        "若需修改车型信息（制造商/型号），请使用「修改车型 <车型ID> ...」\r\n" +
                        "示例：修改车辆 123 等级=S2-900 强度=META+ 调校=\"Nyasmowisher[be my light]\"");
                    return true;
                }

                if (!int.TryParse(parts[0], out int tuneIdAlias))
                {
                    md.Content = "请提供调校 ID（查车结果中的 ID）。";
                    reply.Keyboard = new KeyboardMessage().AppendButtons(2, Button.CreateCmdButton("🖌 重新修改", e.Detail, false));
                    await botService.SendAsync(e, reply);
                    return true;
                }

                VehicleTuneRequest tuneAliasReq = new() { Id = tuneIdAlias };
                string tuneAliasRemaining = string.Join(" ", parts[1..]);
                if (!string.IsNullOrEmpty(tuneAliasRemaining) && !ParseTuneKeyValuePairs(tuneAliasRemaining, tuneAliasReq, out string tuneAliasErr))
                {
                    await botService.SendAsync(e, tuneAliasErr);
                    return true;
                }

                if (!forzaService.UpdateTune(tuneAliasReq, user!.Id, out string tuneAliasUpdErr))
                {
                    md.Content = $"❌ 修改失败：{tuneAliasUpdErr}";
                    reply.Keyboard = new KeyboardMessage().AppendButtons(2, Button.CreateCmdButton("🖌 重新修改", e.Detail, false));
                    await botService.SendAsync(e, reply);
                    return true;
                }

                md.Content = "✅ 调校信息修改成功。";
                reply.Keyboard = new KeyboardMessage().AppendButtons(2, ForzaNavButtons());
                await botService.SendAsync(e, reply);
                return true;
            }

            // 修改调校：按 TuneId 更新 VehicleTunes 显示字段，并置位 IsManual（决策15）
            if (e.Detail.StartsWith("修改调校"))
            {
                if (!await CheckUser(e, user, false))
                    return true;

                string detail = e.Detail["修改调校".Length..].Trim();
                string[] parts = detail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 1 || !int.TryParse(parts[0], out int tuneId))
                {
                    await botService.SendAsync(e,
                        "格式：修改调校 <调校ID> [参数=值]\r\n" +
                        "可用参数：等级= 比赛类型= 驱动= 属性= 胎面= 干湿地= 强度= 调校= 备注= 圈速=\r\n" +
                        "示例：修改调校 123 等级=S2-900 强度=META+ 调校=\"Nyasmowisher[be my light]\"");
                    return true;
                }

                VehicleTuneRequest tuneReq = new() { Id = tuneId };
                string remaining = string.Join(" ", parts[1..]);
                if (!string.IsNullOrEmpty(remaining) && !ParseTuneKeyValuePairs(remaining, tuneReq, out string tuneParseErr))
                {
                    await botService.SendAsync(e, tuneParseErr);
                    return true;
                }

                if (!forzaService.UpdateTune(tuneReq, user!.Id, out string tuneUpdErr))
                {
                    md.Content = $"❌ 修改调校失败：{tuneUpdErr}";
                    reply.Keyboard = new KeyboardMessage().AppendButtons(2, Button.CreateCmdButton("🖌 重新修改", e.Detail, false));
                    await botService.SendAsync(e, reply);
                    return true;
                }

                md.Content = "✅ 调校信息修改成功。";
                reply.Keyboard = new KeyboardMessage().AppendButtons(2, ForzaNavButtons());
                await botService.SendAsync(e, reply);
                return true;
            }

            // 新增车型：仅建 Vehicles 记录（制造商/型号/年份），不含调校字段
            // 必须在 新增车辆 之前匹配，防止前缀被截断
            if (e.Detail.StartsWith("新增车型"))
            {
                if (!await CheckUser(e, user, false))
                    return true;

                string detail = e.Detail["新增车型".Length..].Trim();
                if (string.IsNullOrEmpty(detail))
                {
                    await botService.SendAsync(e,
                        "格式：新增车型 制造商=xxx 型号=xxx [年份=yyyy]\r\n" +
                        "必填字段：制造商、型号\r\n" +
                        "示例：新增车型 制造商=法拉利 型号=F40 年份=1992");
                    return true;
                }

                VehicleModelRequest modelReq = new();
                if (!ParseModelKeyValuePairs(detail, modelReq, out string parseError))
                {
                    await botService.SendAsync(e, parseError);
                    return true;
                }

                if (string.IsNullOrWhiteSpace(modelReq.Manufacturer) || string.IsNullOrWhiteSpace(modelReq.Model))
                {
                    md.Content = "缺少必填字段：制造商、型号都必须提供。";
                    reply.Keyboard = new KeyboardMessage().AppendButtons(1, Button.CreateCmdButton("🖌 重新修改", e.Detail, false));
                    await botService.SendAsync(e, reply);
                    return true;
                }

                int vehicleId = forzaService.EnsureVehicleModel(modelReq, user!.Id, out string addError);
                reply.Keyboard = new KeyboardMessage();
                if (vehicleId > 0)
                {
                    md.Content = $"✅ 车型新增/已存在，车辆ID：{vehicleId}。\r\n可用\"新增调校 车辆={vehicleId} 等级=xx 比赛类型=xx 驱动=xx\"添加调校。";
                }
                else
                {
                    md.Content = $"❌ 新增车型失败：{addError}";
                }
                reply.Keyboard.AppendButtons(2, ForzaNavButtons());
                await botService.SendAsync(e, reply);
                return true;
            }

            // 新增调校：车辆ID必须已存在，等级/比赛类型/驱动为必填
            // 必须在 新增车辆 之前匹配
            if (e.Detail.StartsWith("新增调校"))
            {
                if (!await CheckUser(e, user, false))
                    return true;

                string detail = e.Detail["新增调校".Length..].Trim();
                if (string.IsNullOrEmpty(detail))
                {
                    await botService.SendAsync(e,
                        "格式：新增调校 车辆=<ID> 等级=xxx 比赛类型=xxx 驱动=xxx [属性= 胎面= 干湿地= 强度= 调校= 备注= 圈速=]\r\n" +
                        "必填字段：车辆（车辆ID）、等级、比赛类型、驱动\r\n" +
                        "示例：新增调校 车辆=1 等级=S1 比赛类型=公路 驱动=四驱 强度=META 调校=\"Nyasmowisher[be my light]\"\r\n" +
                        "等级：S2, S1, A, B, C, R 或完整格式 S2-900 等\r\n" +
                        "比赛类型：公路、拉力、泥地、越野、街头等\r\n" +
                        "提示：先用\"新增车型\"获取车辆ID，再用本指令添加调校。");
                    return true;
                }

                VehicleTuneRequest tuneReq = new();
                if (!ParseTuneKeyValuePairs(detail, tuneReq, out string parseError))
                {
                    await botService.SendAsync(e, parseError);
                    return true;
                }

                reply.Keyboard = new KeyboardMessage();
                int tuneId = forzaService.AddTune(tuneReq, user!.Id, out string addError);
                if (tuneId > 0)
                {
                    md.Content = $"✅ 调校新增成功！调校ID：{tuneId}。";
                }
                else
                {
                    md.Content = $"❌ 新增调校失败：{addError}";
                }
                reply.Keyboard.AppendButtons(2, ForzaNavButtons());
                await botService.SendAsync(e, reply);
                return true;
            }

            // 新增车辆：兼容别名，同时创建车型和调校
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
                        "格式：新增车辆 制造商=xxx 型号=xxx 等级=xxx 比赛类型=xxx 驱动=xxx [其他参数]\r\n" +
                        "必填字段：制造商、型号、等级、比赛类型、驱动\r\n" +
                        "可用参数：年份= 属性= 胎面= 干湿地= 强度= 调校= 备注= 圈速=\r\n" +
                        "示例：新增车辆 制造商=法拉利 型号=F40 年份=1992 等级=S1 比赛类型=公路 驱动=四驱 强度=META 调校=\"Nyasmowisher[be my light]\"\r\n" +
                        "等级：S2, S1, A, B, C, R 或完整格式 S2-900 等\r\n" +
                        "比赛类型：公路、拉力、泥地、越野、街头等\r\n" +
                        "提示：也可分两步：先\"新增车型\"，再\"新增调校\"。");
                    return true;
                }

                // 解析所有参数（车型+调校字段混合）
                VehicleModelRequest modelReq2 = new();
                VehicleTuneRequest tuneReq2 = new();
                if (!ParseCombinedVehicleKeyValuePairs(detail, modelReq2, tuneReq2, out string parseError))
                {
                    await botService.SendAsync(e, parseError);
                    return true;
                }

                // 检查必填字段
                if (string.IsNullOrWhiteSpace(modelReq2.Manufacturer) || string.IsNullOrWhiteSpace(modelReq2.Model) ||
                    string.IsNullOrWhiteSpace(tuneReq2.Class) || string.IsNullOrWhiteSpace(tuneReq2.MatchType) ||
                    string.IsNullOrWhiteSpace(tuneReq2.Drivetrain))
                {
                    md.Content = "缺少必填字段：制造商、型号、等级、比赛类型、驱动都必须提供。";
                    reply.Keyboard = new KeyboardMessage().AppendButtons(1, Button.CreateCmdButton("🖌 重新修改", e.Detail, false));
                    await botService.SendAsync(e, reply);
                    return true;
                }

                reply.Keyboard = new KeyboardMessage();

                // 第一步：确保车型存在
                int vid = forzaService.EnsureVehicleModel(modelReq2, user!.Id, out string modelError);
                if (vid == 0)
                {
                    md.Content = $"❌ 新增车型失败：{modelError}";
                    reply.Keyboard.AppendButtons(2, ForzaNavButtons());
                    await botService.SendAsync(e, reply);
                    return true;
                }

                // 第二步：新增调校
                tuneReq2.VehicleId = vid;
                int newTuneId = forzaService.AddTune(tuneReq2, user!.Id, out string tuneError);
                if (newTuneId > 0)
                {
                    md.Content = $"✅ 车辆新增成功！车辆ID：{vid}，调校ID：{newTuneId}。";
                }
                else
                {
                    md.Content = $"❌ 车型已创建（ID：{vid}），但新增调校失败：{tuneError}";
                }

                reply.Keyboard.AppendButtons(2, ForzaNavButtons());
                await botService.SendAsync(e, reply);
                return true;
            }

            // 删除车型：按 VehicleId 删除 Vehicles 行，FK CASCADE 连带清除全部调校
            // 必须在 删除车辆 之前匹配，防止前缀被截断
            if (e.Detail.StartsWith("删除车型"))
            {
                if (!await CheckUser(e, user, false))
                    return true;

                string detail = e.Detail["删除车型".Length..].Trim();
                if (string.IsNullOrEmpty(detail))
                {
                    await botService.SendAsync(e,
                        "格式：删除车型 <车型ID> [id2 id3 ...]\r\n" +
                        "⚠️ 删除车型会同时删除该车型下的所有调校！\r\n" +
                        "示例：删除车型 5\r\n" +
                        "示例：删除车型 5 6 7");
                    return true;
                }

                string[] modelDelParts = detail.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
                List<int> modelIds = [];
                foreach (string p in modelDelParts)
                {
                    if (int.TryParse(p, out int id))
                        modelIds.Add(id);
                    else
                    {
                        md.Content = $"车型ID 必须为数字，无法识别：{p}";
                        reply.Keyboard = new KeyboardMessage().AppendButtons(2, ForzaNavButtons());
                        await botService.SendAsync(e, reply);
                        return true;
                    }
                }

                var (modelDelSuccess, modelDelMsg, _) = forzaService.DeleteVehicleModelsByIds(modelIds);
                md.Content = modelDelSuccess ? $"✅ {modelDelMsg}" : $"❌ {modelDelMsg}";
                reply.Keyboard = new KeyboardMessage().AppendButtons(2, ForzaNavButtons());
                await botService.SendAsync(e, reply);
                return true;
            }

            // 删除调校：按 TuneId 删除 VehicleTunes 行（不影响车型）
            // 必须在 删除车辆 之前匹配
            if (e.Detail.StartsWith("删除调校"))
            {
                if (!await CheckUser(e, user, false))
                    return true;

                string detail = e.Detail["删除调校".Length..].Trim();
                if (string.IsNullOrEmpty(detail))
                {
                    await botService.SendAsync(e,
                        "格式：删除调校 <调校ID> [id2 id3 ...]\r\n" +
                        "示例：删除调校 123\r\n" +
                        "示例：删除调校 1 2 3");
                    return true;
                }

                string[] tuneDelParts = detail.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
                List<int> tuneDelIds = [];
                foreach (string p in tuneDelParts)
                {
                    if (int.TryParse(p, out int id))
                        tuneDelIds.Add(id);
                    else
                    {
                        md.Content = $"调校ID 必须为数字，无法识别：{p}";
                        reply.Keyboard = new KeyboardMessage().AppendButtons(2, ForzaNavButtons());
                        await botService.SendAsync(e, reply);
                        return true;
                    }
                }

                var (tuneDelSuccess, tuneDelMsg, _) = forzaService.DeleteTunesByIds(tuneDelIds);
                md.Content = tuneDelSuccess ? $"✅ {tuneDelMsg}" : $"❌ {tuneDelMsg}";
                reply.Keyboard = new KeyboardMessage().AppendButtons(2, ForzaNavButtons());
                await botService.SendAsync(e, reply);
                return true;
            }

            // 删除车辆（兼容别名）→ 实际委托给「删除调校」语义：
            // 查车结果卡片展示的 ID 是 TuneId，「删除车辆 <id>」等价于「删除调校 <id>」。
            if (e.Detail.StartsWith("确认删除车辆"))
            {
                if (!await CheckUser(e, user, false))
                    return true;

                string detail = e.Detail["确认删除车辆".Length..].Trim();
                // 解析逗号分隔的 TuneId 列表
                List<int> idsToDelete = [];
                foreach (string token in detail.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
                {
                    if (int.TryParse(token, out int id) && id > 0)
                        idsToDelete.Add(id);
                }

                if (idsToDelete.Count == 0)
                {
                    md.Content = "未找到有效的调校 ID，请重新执行删除操作。";
                    await botService.SendAsync(e, reply);
                    return true;
                }

                var (delSuccess, delMsg, _) = forzaService.DeleteTunesByIds(idsToDelete);
                md.Content = delSuccess ? $"✅ 已删除 {idsToDelete.Count} 条调校。" : $"❌ 删除失败：{delMsg}";
                reply.Keyboard = new KeyboardMessage().AppendButtons(2, ForzaNavButtons());
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
                        "格式：删除车辆 <调校ID> [id2 id3 ...]\r\n" +
                        "（调校ID 即查车结果中的 ID；如需删除整个车型及其全部调校，请使用「删除车型 <车型ID>」）\r\n" +
                        "示例：删除车辆 123\r\n" +
                        "示例：删除车辆 1 2 3");
                    return true;
                }

                // 尝试解析为TuneId列表（空格或逗号分隔）
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

                // 情况1：纯数字，按调校ID批量删除（直接执行，无需二次确认）
                if (allAreNumbers && ids.Count > 0)
                {
                    var (success, msg, _) = forzaService.DeleteTunesByIds(ids);
                    md.Content = success ? $"✅ {msg}" : $"❌ {msg}";
                    reply.Keyboard = new KeyboardMessage().AppendButtons(2, ForzaNavButtons());
                    await botService.SendAsync(e, reply);
                    return true;
                }

                // 情况2：非纯数字，提示格式错误（旧的 FindVehiclesByCriteria 路径已废弃，ID 不可跨表混用）
                md.Content = "调校ID 必须为数字。如需按车型条件查找，请先「查车 <关键字>」获取调校ID 后再删除。";
                reply.Keyboard = new KeyboardMessage().AppendButtons(2, ForzaNavButtons());
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
                        "必填字段：中文名、英文名、路线类型（circuit/sprint）、长度（数字）\r\n" +
                        "可用参数：中文名= 英文名= 别名= 长度= 路线类型= 路面= 赛事大类= 赛道特性= 难度= 海拔= 弯道数= 路宽= 圈速= 世界纪录= WR车辆= WR调校= 推荐等级= 推荐车型= 区域= 最佳赛季= 天气= 夜间= DLC= 反向= 图片URL= 小地图URL= 备注=\r\n" +
                        "别名支持逗号/顿号分隔，自动编码为 JSON 数组。含空格的英文名请加引号。\r\n" +
                        "路线类型：circuit=圈图, sprint=冲刺图\r\n" +
                        "路面：asphalt/dirt/gravel/sand/mixed/snow/cross_country\r\n" +
                        "赛事大类：road/dirt/cross_country/street/drag/rally/showcase/mountain_road（峠道）\r\n" +
                        "赛道特性：handling/speed/balanced/offroad/drift/technical\r\n" +
                        "示例：新增赛道 中文名=海滨冲刺 英文名=\"Coastal Sprint\" 路线类型=sprint 长度=8.4 路面=asphalt 赛道特性=speed 难度=2 区域=Pacific Coast");
                    return true;
                }

                MapRequest mapRequest = new();
                if (!ParseMapKeyValuePairs(detail, mapRequest, out string parseError))
                {
                    await botService.SendAsync(e, parseError);
                    return true;
                }

                if (string.IsNullOrEmpty(mapRequest.NameZh) || string.IsNullOrEmpty(mapRequest.NameEn) ||
                    string.IsNullOrEmpty(mapRequest.RouteType) || string.IsNullOrEmpty(mapRequest.LengthKm))
                {
                    md.Content = "缺少必填字段：中文名、英文名、路线类型、长度都必须提供。";
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

                reply.Keyboard = new KeyboardMessage().AppendButtons(2, ForzaNavButtons());
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
                        "可用参数：中文名= 英文名= 别名= 长度= 路线类型= 路面= 赛事大类= 赛道特性= 难度= 海拔= 弯道数= 路宽= 圈速= 世界纪录= WR车辆= WR调校= 推荐等级= 推荐车型= 区域= 最佳赛季= 天气= 夜间= DLC= 反向= 图片URL= 小地图URL= 备注=\r\n" +
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
                reply.Keyboard = new KeyboardMessage().AppendButtons(2, ForzaNavButtons());
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
                    reply.Keyboard = new KeyboardMessage().AppendButtons(2, ForzaNavButtons());
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

                // 将匹配到的 ID 列表编码进确认按钮，关闭 TOCTOU 窗口
                string idListPayload = string.Join(",", matches.Select(m => m.Id.ToString()));

                md.Content = sb.ToString();
                reply.Keyboard = new KeyboardMessage()
                    .AppendButtons(1, Button.CreateCmdButton("✅ 确认删除赛道", "确认删除赛道 " + idListPayload, false));
                await botService.SendAsync(e, reply);
                return true;
            }

            if (e.Detail.StartsWith("确认删除赛道"))
            {
                if (!await CheckUser(e, user, false))
                    return true;

                string detail = e.Detail["确认删除赛道".Length..].Trim();
                // 解析逗号分隔的 ID 列表
                List<int> idsToDelete = [];
                foreach (string token in detail.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
                {
                    if (int.TryParse(token, out int id) && id > 0)
                        idsToDelete.Add(id);
                }

                if (idsToDelete.Count == 0)
                {
                    md.Content = "未找到有效的赛道 ID，请重新执行删除赛道操作。";
                    await botService.SendAsync(e, reply);
                    return true;
                }

                var (delSuccess, delMsg, _) = forzaService.DeleteMapsByIds(idsToDelete);
                if (delSuccess)
                {
                    md.Content = $"✅ 已删除 {idsToDelete.Count} 条赛道。";
                }
                else
                {
                    md.Content = $"❌ 删除失败：{delMsg}";
                }

                reply.Keyboard = new KeyboardMessage().AppendButtons(2, ForzaNavButtons());
                await botService.SendAsync(e, reply);
                return true;
            }

            // ========== 调校缓存管理（管理员） ==========

            if (e.Detail.StartsWith("待更新调校表"))
            {
                if (!await CheckUser(e, user, false))
                    return true;

                string pendingDetail = e.Detail["待更新调校表".Length..].Trim();
                (_, int pendingPage) = SplitPageParam(pendingDetail);

                var (pendingSuccess, pendingMsg, pendingData, pendingTotal) = forzaService.ListTunesByStatus("有更新", pendingPage);
                reply.Keyboard = new KeyboardMessage();

                if (!pendingSuccess || pendingData == null)
                {
                    md.Content = $"暂无「有更新」状态的调校。\r\n{pendingMsg}";
                    reply.Keyboard.AppendButtons(2, ForzaNavButtons());
                    await botService.SendAsync(e, reply);
                    return true;
                }

                StringBuilder pendingSb = new();
                pendingSb.AppendLine($"🔄 待更新调校表（第 {pendingPage}/{pendingTotal} 页）");
                foreach (DataRow tuneRow in pendingData.Rows)
                {
                    int rowTuneId = Convert.ToInt32(tuneRow["Id"]);
                    string rowClass = tuneRow["Class"]?.ToString() ?? "";
                    string rowMatchType = tuneRow["MatchType"]?.ToString() ?? "";
                    string rowDrivetrain = tuneRow["Drivetrain"]?.ToString() ?? "";
                    pendingSb.AppendLine($"\r\n**ID: {rowTuneId} [{rowClass}/{rowMatchType}] {rowDrivetrain}**");

                    var (cacheOk, cacheRow) = forzaService.GetCacheTuneByTuneId(rowTuneId);
                    if (cacheOk && cacheRow != null)
                    {
                        string diff = ForzaService.RenderTuneDiff(tuneRow, cacheRow);
                        pendingSb.AppendLine(string.IsNullOrEmpty(diff) ? "变化：（数据相同）" : $"变化：\r\n{diff.TrimEnd()}");
                    }
                    else
                    {
                        pendingSb.AppendLine("（未找到缓存行）");
                    }

                    reply.Keyboard.AppendButtons(1, Button.CreateCmdButton("🔄 更新", $"更新调校 {rowTuneId}", false));
                }
                md.Content = pendingSb.ToString();
                if (pendingTotal > 1)
                    reply.Keyboard.AddPaginationRow("待更新调校表 -p ", pendingPage, pendingTotal);
                reply.Keyboard.AppendButtonsWithNewRow(2, ForzaNavButtons());
                await botService.SendAsync(e, reply);
                return true;
            }

            if (e.Detail.StartsWith("已过时调校表"))
            {
                if (!await CheckUser(e, user, false))
                    return true;

                string expiredDetail = e.Detail["已过时调校表".Length..].Trim();
                (_, int expiredPage) = SplitPageParam(expiredDetail);

                var (expiredSuccess, expiredMsg, expiredData, expiredTotal) = forzaService.ListTunesByStatus("已过时", expiredPage);
                reply.Keyboard = new KeyboardMessage();

                if (!expiredSuccess || expiredData == null)
                {
                    md.Content = $"暂无「已过时」状态的调校。\r\n{expiredMsg}";
                    reply.Keyboard.AppendButtons(2, ForzaNavButtons());
                    await botService.SendAsync(e, reply);
                    return true;
                }

                StringBuilder expiredSb = new();
                expiredSb.AppendLine($"🗑 已过时调校表（第 {expiredPage}/{expiredTotal} 页）");
                foreach (DataRow tuneRow in expiredData.Rows)
                {
                    int rowTuneId = Convert.ToInt32(tuneRow["Id"]);
                    string rowClass = tuneRow["Class"]?.ToString() ?? "";
                    string rowMatchType = tuneRow["MatchType"]?.ToString() ?? "";
                    string rowDrivetrain = tuneRow["Drivetrain"]?.ToString() ?? "";
                    expiredSb.AppendLine($"\r\n**ID: {rowTuneId} [{rowClass}/{rowMatchType}] {rowDrivetrain}**");

                    reply.Keyboard.AppendButtons(1, Button.CreateCmdButton("🗑 删除", $"删除调校 {rowTuneId}", false));
                }
                md.Content = expiredSb.ToString();
                if (expiredTotal > 1)
                    reply.Keyboard.AddPaginationRow("已过时调校表 -p ", expiredPage, expiredTotal);
                reply.Keyboard.AppendButtonsWithNewRow(2, ForzaNavButtons());
                await botService.SendAsync(e, reply);
                return true;
            }

            if (e.Detail.StartsWith("更新调校"))
            {
                if (!await CheckUser(e, user, false))
                    return true;

                string applyStr = e.Detail["更新调校".Length..].Trim();
                if (!int.TryParse(applyStr, out int applyTuneId))
                {
                    await botService.SendAsync(e, "格式：更新调校 <调校ID>\r\n示例：更新调校 123");
                    return true;
                }

                bool applyOk = forzaService.ApplyCachedTune(applyTuneId, user!.Id, out string applyErr);
                md.Content = applyOk
                    ? "✅ 调校已从缓存更新，状态重置为正常。"
                    : $"❌ 更新失败：{applyErr}";
                reply.Keyboard = new KeyboardMessage().AppendButtons(2, ForzaNavButtons());
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
                    request.RaceCategory = val == "峠道" ? "mountain_road" : val;
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

    /// <summary>
    /// 解析新增车型指令的键值对：支持 制造商/型号/年份。
    /// </summary>
    private static bool ParseModelKeyValuePairs(string input, VehicleModelRequest request, out string error)
    {
        error = "";
        System.Text.RegularExpressions.MatchCollection matches = GetParamValue().Matches(input);
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            string key = m.Groups[1].Value.ToLowerInvariant();
            string val = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            switch (key)
            {
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
                default:
                    error = $"未知参数：{key}（新增车型仅支持：制造商、型号、年份）";
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 解析新增调校指令的键值对：支持 车辆(ID)/等级/比赛类型/驱动 及可选展示字段。
    /// </summary>
    private static bool ParseTuneKeyValuePairs(string input, VehicleTuneRequest request, out string error)
    {
        error = "";
        System.Text.RegularExpressions.MatchCollection matches = GetParamValue().Matches(input);
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            string key = m.Groups[1].Value.ToLowerInvariant();
            string val = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            switch (key)
            {
                case "车辆":
                case "vehicleid":
                    if (int.TryParse(val, out int vid))
                        request.VehicleId = vid;
                    else
                    {
                        error = "车辆参数必须为整数（车辆ID）。";
                        return false;
                    }
                    break;
                case "等级":
                case "class":
                    request.Class = val;
                    break;
                case "比赛类型":
                case "matchtype":
                    request.MatchType = val;
                    break;
                case "传动":
                case "驱动":
                case "drivetrain":
                    request.Drivetrain = val;
                    break;
                case "属性":
                case "performancepreference":
                    request.PerformancePreference = val;
                    break;
                case "胎面":
                case "tire":
                case "tirecompound":
                    request.TireCompound = val;
                    break;
                case "干湿地":
                case "surface":
                case "surfacecondition":
                    request.SurfaceCondition = val;
                    break;
                case "强度":
                case "strength":
                    request.Strength = val;
                    break;
                case "调校":
                case "tuneraw":
                case "tune":
                    request.TuneRaw = val;
                    break;
                case "备注":
                case "notes":
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

    /// <summary>
    /// 解析新增车辆（兼容别名）指令的键值对：同时填充车型与调校请求。
    /// 支持 制造商/型号/年份（车型字段）以及所有调校字段。
    /// </summary>
    private static bool ParseCombinedVehicleKeyValuePairs(string input, VehicleModelRequest modelReq, VehicleTuneRequest tuneReq, out string error)
    {
        error = "";
        System.Text.RegularExpressions.MatchCollection matches = GetParamValue().Matches(input);
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            string key = m.Groups[1].Value.ToLowerInvariant();
            string val = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            switch (key)
            {
                case "制造商":
                case "manufacturer":
                    modelReq.Manufacturer = val;
                    break;
                case "型号":
                case "model":
                    modelReq.Model = val;
                    break;
                case "年份":
                case "year":
                    modelReq.Year = val;
                    break;
                case "等级":
                case "class":
                    tuneReq.Class = val;
                    break;
                case "比赛类型":
                case "matchtype":
                    tuneReq.MatchType = val;
                    break;
                case "传动":
                case "驱动":
                case "驱动类型":
                case "drivetrain":
                    tuneReq.Drivetrain = val;
                    break;
                case "属性":
                case "performancepreference":
                    tuneReq.PerformancePreference = val;
                    break;
                case "胎面":
                case "tire":
                case "tirecompound":
                    tuneReq.TireCompound = val;
                    break;
                case "干湿地":
                case "适用干/湿":
                case "surface":
                case "surfacecondition":
                    tuneReq.SurfaceCondition = val;
                    break;
                case "强度":
                case "strength":
                    tuneReq.Strength = val;
                    break;
                case "调校":
                case "tuneraw":
                case "tune":
                    tuneReq.TuneRaw = val;
                    break;
                case "备注":
                case "notes":
                case "remarks":
                    tuneReq.Remarks = val;
                    break;
                case "圈速":
                case "laptime":
                    tuneReq.LapTime = val;
                    break;
                default:
                    error = $"未知参数：{key}";
                    return false;
            }
        }
        return true;
    }

    private static List<Button> ForzaNavButtons() => [
        Button.CreateCmdButton("🌟 地平线", "地平线", false),
        Button.CreateCmdButton("🏎 查车", "查车", false),
        Button.CreateCmdButton("🛣️ 查赛道", "查赛道", false),
        Button.CreateCmdButton("📊 查等级", "查等级", false),
    ];
}
