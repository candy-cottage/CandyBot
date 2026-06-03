using CandyCottage.CandyBot.Models;

namespace CandyCottage.CandyBot.Services.BotServices
{
    public interface IBotReplySender
    {
        public Task<SendMessageResult?> SendAsync(IBotMessage msg, BotReply reply, int? msgSeq = null);
        public Task<bool> RecallMessageAsync(IBotMessage msg, bool hideTip = false);
    }
}
