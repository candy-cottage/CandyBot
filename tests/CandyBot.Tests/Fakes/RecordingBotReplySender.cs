using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Services.BotServices;

namespace CandyBot.Tests.Fakes;

/// <summary>
/// 测试用的回复发送器：不调用 QQ HTTP，仅把每次回复记录到内存列表，供断言中文回复内容与键盘指令使用。
/// </summary>
public sealed class RecordingBotReplySender : IBotReplySender
{
    public readonly record struct SentReply(IBotMessage Message, BotReply Reply, int? MsgSeq);

    private readonly List<SentReply> _sent = [];

    public IReadOnlyList<SentReply> Sent => _sent;

    public SentReply? Last => _sent.Count > 0 ? _sent[^1] : null;

    /// <summary>
    /// 每条回复的文本内容。优先取纯文本 <see cref="BotReply.Text"/>；多数回复经由 string→BotReply 隐式转换写入 Markdown.Content，故回退到该字段。
    /// </summary>
    public IEnumerable<string?> TextContents => _sent.Select(s => s.Reply.Text ?? s.Reply.Markdown?.Content);

    public Task SendAsync(IBotMessage msg, BotReply reply, int? msgSeq = null)
    {
        _sent.Add(new SentReply(msg, reply, msgSeq));
        return Task.CompletedTask;
    }
}
