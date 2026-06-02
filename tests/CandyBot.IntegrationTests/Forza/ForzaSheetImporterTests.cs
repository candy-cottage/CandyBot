using System.Data;
using System.Text.Json;
using CandyBot.IntegrationTests.Infrastructure;
using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Services;
using CandyCottage.CandyBot.Utilities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CandyBot.IntegrationTests.Forza;

[Collection(MySqlCollection.Name)]
[Trait("Category", "MySql")]
public class ForzaSheetImporterTests(MySqlFixture fixture)
{
    private readonly MySqlFixture _fixture = fixture;

    private static Dictionary<string, List<List<string>>> LoadFixture()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Forza", "DSXRieHNsZXpFZmZ4.json");
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<string, List<List<string>>>>(json)!;
    }

    [Fact]
    public async Task SyncLoadedSheetsAsync_完整真实Json写入157辆且重跑仍为157辆()
    {
        await _fixture.ResetDataAsync();
        Dictionary<string, List<List<string>>> sheets = LoadFixture();

        using MySQLHelper sql = _fixture.CreateHelper();
        ForzaService forzaService = new(NullLogger<ForzaService>.Instance, sql);
        ForzaSheetImporter importer = new(
            NullLogger<ForzaSheetImporter>.Instance,
            sheetService: null!,
            sql,
            forzaService,
            new NoOpSheetParseDiagnostic());

        ForzaSheetSyncReport first = await importer.SyncLoadedSheetsAsync(sheets);
        ForzaSheetSyncReport second = await importer.SyncLoadedSheetsAsync(sheets);

        first.Success.Should().BeTrue(first.ErrorMessage);
        first.InsertedVehicleCount.Should().Be(157);
        second.Success.Should().BeTrue(second.ErrorMessage);
        second.InsertedVehicleCount.Should().Be(157);

        DataRow? row = sql.ExecuteDataRow("SELECT COUNT(*) FROM Vehicles");
        Convert.ToInt32(row![0]).Should().Be(157);
    }

    [Fact]
    public async Task SyncLoadedSheetsAsync_解析零行时拒绝覆盖旧数据()
    {
        await _fixture.ResetDataAsync();
        using MySQLHelper sql = _fixture.CreateHelper();
        ForzaService forzaService = new(NullLogger<ForzaService>.Instance, sql);
        forzaService.AddVehicle(new VehicleRequest
        {
            Manufacturer = "Seed",
            Model = "KeepMe",
            Class = "A",
            MatchType = "公路",
            Drivetrain = "四驱",
        }, out string seedError).Should().BeTrue(seedError);

        ForzaSheetImporter importer = new(
            NullLogger<ForzaSheetImporter>.Instance,
            sheetService: null!,
            sql,
            forzaService,
            new NoOpSheetParseDiagnostic());

        ForzaSheetSyncReport report = await importer.SyncLoadedSheetsAsync([]);

        report.Success.Should().BeFalse();
        report.ErrorMessage.Should().Contain("解析结果为 0");
        DataRow? row = sql.ExecuteDataRow("SELECT COUNT(*) FROM Vehicles");
        Convert.ToInt32(row![0]).Should().Be(1);
    }
}
