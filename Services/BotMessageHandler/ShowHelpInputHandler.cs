using System.Text;
using CandyCottage.CandyBot.Models;
using Milimoe.FunGame.Core.Api.Utility;
using Milimoe.FunGame.Core.Entity;

namespace CandyCottage.CandyBot.Services;

public partial class CandyBotService
{
    private const int PageSize = 12;
    private readonly static string[] SupportModules = ["系统帮助", "住宅帮助", "商铺帮助", "仓库帮助", "工厂帮助", "物流帮助", "停车场帮助", "经济帮助", "管理员帮助"];

    public static Dictionary<string, string> GeneralHelp { get; } = new()
    {
        { "糖果屋", "查看糖果屋仪表盘" },
        { "系统帮助", "查看存档、创建角色等相关指令" },
        { "住宅帮助", "购买、出售、查看住宅" },
        { "商铺帮助", "购买、管理商铺，上架货物，设置采购" },
        { "仓库帮助", "购买仓库，查看库存，转移货物" },
        { "工厂帮助", "购买工厂，管理流水线，生产货物" },
        { "物流帮助", "查看货车，规划运输路线" },
        { "停车场帮助", "购买停车场，购买货车，收取停车费" },
        { "经济帮助", "经营报告，缴清维护费，关闭/开启商铺" },
        { "管理员帮助", "仅管理员可访问，刷新市场、重置数据等" }
    };

    public static Dictionary<string, string> SystemHelp { get; } = new()
    {
        { "糖果屋", "查看糖果屋仪表盘" },
        { "创建存档", "成为糖果屋店长的第一步" },
        { "我的资产", "查看所有建筑、货车、库存明细" },
        { "市场行情", "查看本周货物价格及销量预测" },
        { "修改昵称为", "修改游戏中显示的名称" },
        { "帮助", "显示游戏帮助菜单" }
    };

    public static Dictionary<string, string> ResidenceHelp { get; } = new()
    {
        { "买住宅", "购买住宅（基础/初级/中级/高级/别墅）" },
        { "我的住宅", "列出已拥有的住宅" },
        { "出售住宅", "参数：<住宅ID>，出售指定住宅，获得购买价50%" }
    };

    public static Dictionary<string, string> ShopHelp { get; } = new()
    {
        { "买商铺", "购买商铺（基础/中型/大型/超市/商场）" },
        { "我的商铺", "列出所有商铺及状态" },
        { "管理商铺", "参数：<商铺ID>，进入商铺管理面板" },
        { "上架", "参数：<商铺ID> <货物ID> <数量>，将库存货物上架到指定商铺" },
        { "下架", "参数：<商铺ID> <货架位置>，从货架下架货物" },
        { "设置采购", "参数：<商铺ID> <货物ID> <日件数> <最大单价>，自动采购货物" },
        { "出售商铺", "参数：<商铺ID>，出售商铺，获得50%购买价" }
    };

    public static Dictionary<string, string> WarehouseHelp { get; } = new()
    {
        { "买仓库", "购买仓库（小型/中型/大型）" },
        { "我的仓库", "列出所有仓库及占用容量" },
        { "仓库库存", "参数：<仓库ID>，查看仓库内货物清单" },
        { "转移库存", "在仓库/商铺/工厂间转移货物（引导式操作）" },
        { "出售仓库", "参数：<仓库ID>，出售仓库，获得50%购买价" }
    };

    public static Dictionary<string, string> FactoryHelp { get; } = new()
    {
        { "买工厂", "购买工厂（小型/中型/大型）" },
        { "我的工厂", "列出所有工厂及流水线状态" },
        { "管理工厂", "参数：<工厂ID>，进入工厂管理面板" },
        { "设置配方", "参数：<工厂ID> <流水线编号> <货物ID>，指定流水线生产配方" },
        { "启动流水线", "参数：<工厂ID> <流水线编号>，开始生产" },
        { "停止流水线", "参数：<工厂ID> <流水线编号>，停止生产，暂停维护费" },
        { "设置原料采购", "参数：<工厂ID> <原料ID> <日件数> <最大单价>，自动购买原料" },
        { "出售工厂", "参数：<工厂ID>，出售工厂，获得50%购买价" }
    };

    public static Dictionary<string, string> LogisticsHelp { get; } = new()
    {
        { "我的货车", "查看所有货车及归属/任务状态" },
        { "规划路线", "参数：<货车ID>，为货车设置运输路线" },
        { "查看路线", "参数：<货车ID>，查看当前路线安排" },
        { "删除路线", "参数：<货车ID>，删除货车路线" }
    };

    public static Dictionary<string, string> ParkingHelp { get; } = new()
    {
        { "买停车场", "购买停车场（只能拥有一处）" },
        { "我的停车场", "查看车位使用、停车收入、货车列表" },
        { "购买货车", "为停车场购买额外货车（30万/辆）" },
        { "收取停车费", "手动收取累积的挂机收益" },
        { "出售停车场", "出售停车场及所有额外货车" }
    };

    public static Dictionary<string, string> EconomyHelp { get; } = new()
    {
        { "经营报告", "查看昨日/本周收益与支出" },
        { "缴清维护费", "手动缴纳欠费" },
        { "关闭商铺", "参数：<商铺ID>，停止商铺运营，不再扣维护费" },
        { "开启商铺", "参数：<商铺ID>，重新开启已关闭的商铺" }
    };

    public static Dictionary<string, string> AdminHelp { get; } = new()
    {
        { "刷新市场", "强制刷新货物供需关系" },
        { "重置玩家数据", "参数：<QQ号或UID>，清空玩家所有游戏数据" },
        { "发放金币", "参数：<玩家ID> <数量>，发放测试金币" },
        { "设置货物价格", "参数：<货物ID> <采购价> <售价>，调整市场价" },
        { "同步车辆表", "后台同步所有已配置调校表格（幂等），完成后通知触发者" },
        { "新增车型 制造商=x 型号=y [年份=z]", "手动新增一条车型记录" },
        { "新增调校 车辆=<VehicleId> 等级=x 比赛类型=y 驱动=z [属性= 胎面= 干湿地= 强度= 调校= 备注= 圈速=]", "为指定车型新增一条调校记录" },
        { "新增车辆 制造商=x 型号=y 等级=z 比赛类型=w 驱动=v [...]", "一次性新增车型并建立调校（新增车型的别名组合）" },
        { "修改车型 <VehicleId> [字段=值]", "修改车型基本信息（制造商/型号/年份等）" },
        { "修改调校 <TuneId> [字段=值]", "修改调校记录的任意字段" },
        { "删除车型 <VehicleId>", "删除车型及其所有调校（不可恢复）" },
        { "删除调校 <TuneId>", "删除单条调校记录" },
        { "待更新调校表 [页码]", "列出本地库与表格数据有差异、等待更新的调校" },
        { "已过时调校表 [页码]", "列出源表格中已不存在、本地仍有缓存的过时调校" },
        { "更新调校 <TuneId>", "将指定调校从缓存同步写入正式记录" }
    };

    public async Task<bool> HandleShowHelpInput(IBotMessage e, User? user)
    {
        long uid = user?.Id ?? 0;
        MarkdownMessage md = new() { Content = _busy };
        BotReply reply = new() { Markdown = md };

        try
        {
            string detail = e.Detail.Trim();

            // 通用帮助入口：显示模块列表
            if (detail == "帮助")
            {
                StringBuilder sb = new();
                sb.AppendLine("🍬 **欢迎来到糖果屋！** 以下为各模块帮助入口：\n");
                sb.AppendLine("> 点击按钮或直接输入指令名称查看详细帮助\n");
                foreach (var item in GeneralHelp)
                {
                    sb.AppendLine($"- {item.Key.CreateCmdInput()}：{item.Value}");
                }
                sb.AppendLine($"\n*示例：输入 {"商铺帮助".CreateCmdInput()} 查看所有商铺相关指令*");
                md.Content = sb.ToString();
                reply.Keyboard = new KeyboardMessage().AppendButtons(2,
                    Button.CreateCmdButton("🍬 糖果屋", "糖果屋"),
                    Button.CreateCmdButton("🏠 住宅帮助", "住宅帮助"),
                    Button.CreateCmdButton("🏪 商铺帮助", "商铺帮助"),
                    Button.CreateCmdButton("📦 仓库帮助", "仓库帮助"),
                    Button.CreateCmdButton("🏭 工厂帮助", "工厂帮助"),
                    Button.CreateCmdButton("🚚 物流帮助", "物流帮助"),
                    Button.CreateCmdButton("🅿️ 停车场帮助", "停车场帮助"),
                    Button.CreateCmdButton("💰 经济帮助", "经济帮助"),
                    Button.CreateCmdButton("🔧 系统帮助", "系统帮助"),
                    Button.CreateCmdButton("👑 管理员帮助", "管理员帮助")
                );
                await botService.SendAsync(e, reply);
                return true;
            }

            // 解析模块和页码
            string module = "";
            int page = 1;
            string remainder = detail;

            // 匹配已知模块前缀
            foreach (string m in SupportModules)
            {
                if (detail.StartsWith(m))
                {
                    module = m;
                    remainder = detail[m.Length..].Trim();
                    break;
                }
            }

            if (string.IsNullOrEmpty(module))
            {
                return false;
            }

            // 提取页码
            if (int.TryParse(remainder, out int p) && p > 0)
            {
                page = p;
            }

            // 获取对应的字典
            Dictionary<string, string>? helpDict = module switch
            {
                "系统帮助" => SystemHelp,
                "住宅帮助" => ResidenceHelp,
                "商铺帮助" => ShopHelp,
                "仓库帮助" => WarehouseHelp,
                "工厂帮助" => FactoryHelp,
                "物流帮助" => LogisticsHelp,
                "停车场帮助" => ParkingHelp,
                "经济帮助" => EconomyHelp,
                "管理员帮助" => AdminHelp,
                _ => null
            };

            if (helpDict == null)
            {
                await botService.SendAsync(e, "未找到相关帮助信息。");
                return true;
            }

            if (helpDict == AdminHelp && !await CheckUser(e, user, false))
            {
                return true;
            }

            // 分页显示
            int maxPage = helpDict.MaxPage(PageSize);
            if (page > maxPage) page = maxPage;
            var pageItems = helpDict.GetPage(page, PageSize);

            StringBuilder sbHelp = new();
            sbHelp.AppendLine($"📖 **{module}** [ {page}/{maxPage} ]\n");
            int index = 1;
            foreach (var item in pageItems)
            {
                sbHelp.AppendLine($"{index++}. {item.Key.CreateCmdInput()}：{item.Value}");
            }
            md.Content = sbHelp.ToString();
            reply.Keyboard = new KeyboardMessage().AddPaginationRow(module, page, maxPage);
            await botService.SendAsync(e, reply);
            return true;
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Error)) logger.LogError("Error: {ex}", ex);
            await botService.SendAsync(e, _busy);
            return true;
        }
    }
}
