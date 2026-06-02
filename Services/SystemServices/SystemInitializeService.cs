using CandyCottage.CandyBot.Modules;
using CandyCottage.CandyBot.Utilities;
using Milimoe.FunGame.Core.Library.Constant;

namespace CandyCottage.CandyBot.Services.SystemServices
{
    public class SystemInitializeService(ILogger<SystemInitializeService> logger, CandyBotConfig candybotConfig, SQLConnectionConfig sqlConfig, BotConfig botConfig, CandyBotFunctionConfig functionConfig, TencentDocsConfig tencentDocsConfig, DeepSeekConfig deepSeekConfig, CharacterModule characterModule, ItemModule itemModule, SkillModule skillModule) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("正在初始化 . . .");
            logger.LogInformation("读取配置文件 . . .");
            candybotConfig.Init();
            sqlConfig.Init();
            botConfig.Init();
            functionConfig.Init();
            tencentDocsConfig.Init();
            deepSeekConfig.Init();
            logger.LogInformation("加载 FunGame 模组 . . .");
            characterModule.Load();
            itemModule.Load();
            skillModule.Load();
            logger.LogInformation("设置游戏平衡常数 . . .");
            General.GameplayEquilibriumConstant.InGameCurrency = "金币";
            General.GameplayEquilibriumConstant.InGameMaterial = "糖果券";
            General.GameplayEquilibriumConstant.InGameTime = "虚秒";
            logger.LogInformation("初始化完毕！");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
