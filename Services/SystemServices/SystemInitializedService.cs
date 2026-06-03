using System.Data;
using CandyCottage.CandyBot.Interfaces;
using CandyCottage.CandyBot.Utilities;
using Milimoe.FunGame.Core.Api.Transmittal;
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

            taskManager.AddScheduledTask(new("0点同步车辆", new TimeSpan(0, 0, 0), async () =>
            {
                await SyncForzaWithDocs();
            }));
            taskManager.AddScheduledTask(new("12点同步车辆", new TimeSpan(12, 0, 0), async () =>
            {
                await SyncForzaWithDocs();
            }));
        }

        public async Task SyncForzaWithDocs()
        {
            // 1. todo: call sync.

            // 2. cache the Manufacturers
            using IServiceScope scope = serviceProvider.CreateScope();
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
