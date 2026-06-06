using System.Data;
using CandyCottage.CandyBot.Interfaces;
using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Services.BotServices;
using CandyCottage.CandyBot.Utilities;
using Milimoe.FunGame.Core.Api.Transmittal;
using Milimoe.FunGame.Core.Api.Utility;
using Milimoe.FunGame.Core.Library.SQLScript.Common;

namespace CandyCottage.CandyBot.Services.SystemServices
{
    public class SystemInitializedService(IServiceProvider serviceProvider, ITaskSchedulerManager taskManager, ForzaConfig forzaConfig) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            using IServiceScope scope = serviceProvider.CreateScope();
            using SQLHelper sql = scope.ServiceProvider.GetRequiredService<SQLHelper>();
            await sql.ExecuteAsync(ServerLoginLogs.Insert_ServerLoginLog(sql, "CandyBot", ""));

            taskManager.AddScheduledTask(new("0点同步车辆", new TimeSpan(0, 0, 0), RunAsync(SyncForzaWithDocs)), false);
            taskManager.AddScheduledTask(new("12点同步车辆", new TimeSpan(12, 0, 0), RunAsync(SyncForzaWithDocs)), false);
        }

        /// <summary>
        /// FunGame 任务计划只接收 <see cref="Action"/>；这里同步等待异步任务，使调度器能正确记录耗时、阻止重入并捕获异常。
        /// </summary>
        private static System.Action RunAsync(Func<Task> action)
        {
            return () => action().GetAwaiter().GetResult();
        }

        public async Task SyncForzaWithDocs()
        {
            using IServiceScope scope = serviceProvider.CreateScope();
            IBotReplySender botReplySender = scope.ServiceProvider.GetRequiredService<IBotReplySender>();
            PluginConfig initializationConfig = new("sqlconfig", "initialization");
            initializationConfig.LoadConfig();
            string serverAdmin = initializationConfig.Get<string>("admin") ?? "";

            // 1. call sync.
            foreach (string docsName in forzaConfig.DocsID.Keys)
            {
                string fileId = forzaConfig.DocsID[docsName];
                IForzaSheetImporter forzaSheetImporter = scope.ServiceProvider.GetRequiredService<IForzaSheetImporter>();
                ForzaSheetSyncReport report = await forzaSheetImporter.SyncAsync(fileId);
                MarkdownMessage md = new()
                {
                    Content = ""
                };
                BotReply reply = new()
                {
                    Markdown = md
                };
                if (report.Success)
                {
                    md.Content =
                        $"**文件：{fileId}**\r\n" +
                        $"✅ 已同步 {report.InsertedVehicleCount} 辆车辆。\r\n" +
                        $"原表：{report.PreviousVehicleCount} 辆；解析：{report.ParsedVehicleCount} 辆；工作表：{report.ParsedSheetNames.Count} 张。\r\n" +
                        $"跳过工作表：{report.SkippedSheetNames.Count} 张；跳过行：{report.SkippedRows.Count} 行。" +
                        (report.Diagnostics.Count > 0 ? "\r\n诊断：" + string.Join("\r\n", report.Diagnostics.Take(3)) : "");
                }
                else
                {
                    md.Content =
                        $"**文件：{fileId}**\r\n" +
                        $"❌ 同步车辆表失败：{report.ErrorMessage}\r\n" +
                        $"旧数据已保留。当前原表记录：{report.PreviousVehicleCount} 辆；解析：{report.ParsedVehicleCount} 辆。";
                }
                if (serverAdmin != "")
                {
                    await botReplySender.SendAsync(new C2CMessage()
                    {
                        SendProactive = true,
                        Author = new()
                        {
                            Id = serverAdmin
                        }
                    }, reply);
                }
            }

            // 2. cache the Manufacturers
            using SQLHelper sql = scope.ServiceProvider.GetRequiredService<SQLHelper>();
            await sql.ExecuteDataSetAsync("Select Distinct Manufacturer From Vehicles");
            if (sql.Success)
            {
                string[] manufacturers = [.. sql.DataSet.Tables[0].AsEnumerable().Select(r => r["Manufacturer"].ToString() ?? "")];
                forzaConfig.Update(manufacturers);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
