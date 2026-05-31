using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Services.BotServices;

namespace CandyCottage.CandyBot.Utilities
{
    public class Tests(IBotMessage msg, QQBotService service)
    {
        public async Task Text()
        {
            await service.SendTextAsync(msg, $"收到消息了，你发送的消息是：{msg.Detail}");
        }

        public async Task Image(string url)
        {
            // 注意 url 必须是公网可访问的地址
            UploadMediaResult uploadMediaResult = await service.UploadC2CMediaAsync(msg.OpenId, 1, url);
            if (string.IsNullOrEmpty(uploadMediaResult.Error))
            {
                await service.SendTextAsync(msg, $"收到消息了，你发送的消息是：{msg.Detail}");
                // 发多条消息时，记得改 msgSeq，否则会被服务器去重
                await service.SendTextAsync(msg, $"配图", 7, new { file_info = uploadMediaResult.FileInfo }, 2);
            }
        }

        public async Task Markdown()
        {
            BotReply reply = new()
            {
                Markdown = new()
                {
                    Content = "你好啊，\n<qqbot-cmd-enter text=\"/这是蓝色链接测试\"/>"
                },
                Keyboard = new KeyboardMessage().AppendButtons(3, [
                    Button.CreateCmdButton("按钮1", "点击了按钮1", style: 0),
                    Button.CreateCmdButton("按钮2", "点击了按钮2"),
                    Button.CreateInteractionButton("按钮3", "点击了按钮3", "点过了")
                ])
            };
            await service.SendAsync(msg, reply);
        }
    }
}
