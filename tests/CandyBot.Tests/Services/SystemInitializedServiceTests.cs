using System.Data;
using CandyBot.Tests.Utilities;
using CandyCottage.CandyBot.Interfaces;
using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Services.SystemServices;
using CandyCottage.CandyBot.Utilities;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Milimoe.FunGame.Core.Api.Transmittal;
using Milimoe.FunGame.Core.Api.Utility;
using Milimoe.FunGame.Core.Library.Constant;
using NSubstitute;
using Xunit;

namespace CandyBot.Tests.Services;

[Collection(PluginConfigCollection.Name)]
public class SystemInitializedServiceTests
{
    [Fact]
    public async Task 车辆同步计划任务应等待异步导入完成()
    {
        string originalRoot = PluginConfig.RootPath;
        string tmp = Path.Combine(Path.GetTempPath(), "candybot-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            PluginConfig.RootPath = tmp;
            const string fileId = "forza-doc";
            TaskCompletionSource<ForzaSheetSyncReport> syncCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            bool syncStarted = false;

            IForzaSheetImporter importer = Substitute.For<IForzaSheetImporter>();
            importer.SyncAsync(fileId, Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    syncStarted = true;
                    return syncCompletion.Task;
                });

            ServiceCollection services = new();
            services.AddScoped(_ => CreateSql());
            services.AddScoped(_ => Substitute.For<IBotReplySender>());
            services.AddScoped(_ => importer);
            await using ServiceProvider provider = services.BuildServiceProvider();

            TaskSchedulerManager taskManager = new();
            ForzaConfig forzaConfig = new()
            {
                DocsID = { ["ZGC"] = fileId }
            };
            SystemInitializedService service = new(provider, taskManager, forzaConfig);

            await service.StartAsync(CancellationToken.None);
            var scheduledTask = taskManager.GetScheduledTask("0点同步车辆");

            scheduledTask.Should().NotBeNull();
            Task actionRun = Task.Run(scheduledTask!.Action);
            await WaitUntilAsync(() => syncStarted);

            actionRun.IsCompleted.Should().BeFalse("计划任务 Action 必须覆盖异步导入的完整生命周期");

            syncCompletion.SetResult(new ForzaSheetSyncReport(
                Success: true,
                PreviousVehicleCount: 0,
                ParsedVehicleCount: 1,
                InsertedTuneCount: 1,
                ParsedSheetNames: ["S1-公路"],
                SkippedSheetNames: [],
                SkippedRows: [],
                Diagnostics: [],
                ErrorMessage: null));

            await actionRun.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            PluginConfig.RootPath = originalRoot;
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
        }
    }

    private static SQLHelper CreateSql()
    {
        SQLHelper sql = Substitute.For<SQLHelper>();
        sql.Parameters.Returns(new Dictionary<string, object>());
        sql.ExecuteAsync(Arg.Any<string>()).Returns(Task.FromResult(1));
        sql.ExecuteDataSetAsync(Arg.Any<string>()).Returns(Task.FromResult(new DataSet()));
        sql.Result.Returns(SQLResult.NotFound);
        return sql;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
