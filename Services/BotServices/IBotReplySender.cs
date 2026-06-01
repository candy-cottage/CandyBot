using CandyCottage.CandyBot.Models;

namespace CandyCottage.CandyBot.Services.BotServices
{
    public interface IBotReplySender
    {
        Task SendAsync(IBotMessage msg, BotReply reply, int? msgSeq = null);
    }
}
