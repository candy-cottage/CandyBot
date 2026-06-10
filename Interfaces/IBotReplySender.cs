using CandyCottage.CandyBot.Models;

namespace CandyCottage.CandyBot.Interfaces
{
    public interface IBotReplySender
    {
        public Task<SendMessageResult?> SendAsync(IBotMessage msg, BotReply reply, int? msgSeq = null);
        public Task<bool> RecallMessageAsync(IBotMessage msg, bool hideTip = false);

        /// <summary>
        /// 发送主动消息：根据 openId / isGroup 组装代发消息（SendProactive）后走 <see cref="SendAsync"/>。
        /// 默认实现统一了消息结构的拼装，实现类（含测试替身）无需各自复制。
        /// </summary>
        public async Task SendActiveMessageAsync(string openId, bool isGroup, BotReply reply, int? msgSeq = null)
        {
            IBotMessage msg = isGroup ? new GroupMessage()
            {
                SendProactive = true,
                GroupOpenId = openId,
            } : new C2CMessage()
            {
                SendProactive = true,
                Author = new()
                {
                    Id = openId
                }
            };
            await SendAsync(msg, reply, msgSeq);
        }
    }
}
