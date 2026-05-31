using Milimoe.FunGame.Core.Api.Transmittal;
using Milimoe.FunGame.Core.Library.SQLScript.Common;

namespace CandyCottage.CandyBot.Services.SystemServices
{
    public class SystemInitializedService(IServiceProvider serviceProvider) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            using IServiceScope scope = serviceProvider.CreateScope();
            SQLHelper sql = scope.ServiceProvider.GetRequiredService<SQLHelper>();
            await sql.ExecuteAsync(ServerLoginLogs.Insert_ServerLoginLog(sql, "CandyBot", ""));
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
