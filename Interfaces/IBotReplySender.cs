using CandyCottage.CandyBot.Models;

namespace CandyCottage.CandyBot.Interfaces
{
    public interface IBotReplySender
    {
        public Task SendActiveMessageAsync(string openId, bool isGroup, BotReply reply, int? msgSeq = null);
        public Task<SendMessageResult?> SendAsync(IBotMessage msg, BotReply reply, int? msgSeq = null);
        public Task<bool> RecallMessageAsync(IBotMessage msg, bool hideTip = false);
    }
}
