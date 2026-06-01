using CandyCottage.CandyBot.Models;
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
