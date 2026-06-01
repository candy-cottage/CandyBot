using CandyCottage.CandyBot.Models;
using Milimoe.FunGame.Core.Entity;

namespace CandyCottage.CandyBot.Services;

public partial class CandyBotService
{
    public async Task<bool> HandleCandyCottageInput(IBotMessage e, User? user)
    {
        long uid = user?.Id ?? 0;
        MarkdownMessage md = new() { Content = _busy };
        BotReply reply = new() { Markdown = md };

        if (!WillGoOn(functionConfig.CandyCottage, user))
        {
            md.Content = "糖果屋正在装修中，为您带来不便敬请谅解～";
            await botService.SendAsync(e, reply);
            return true;
        }

        try
        {
            if (e.Detail == "糖果屋" || e.Detail == "我的存档")
            {
                md.Content = userService.GetUserInfo(uid);
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
