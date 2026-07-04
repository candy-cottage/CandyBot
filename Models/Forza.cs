namespace CandyCottage.CandyBot.Models
{
    public class VehicleRequest
    {
        public int? Id { get; set; }
        public string? Manufacturer { get; set; }
        public string? Model { get; set; }
        public string? Year { get; set; }
        public string? Class { get; set; }
        public string? MatchType { get; set; }
        public string? Drivetrain { get; set; }
        public string? Attribute { get; set; }
        public string? TireCompound { get; set; }
        public string? SurfaceCondition { get; set; }
        public string? Strength { get; set; }
        public string? Tune { get; set; }
        public string? Remarks { get; set; }
        public string? LapTime { get; set; }
        public string? ExtData { get; set; }
    }

    public class MapRequest
    {
        public int? Id { get; set; }
        public string? NameZh { get; set; }
        public string? NameEn { get; set; }
        public string? Aliases { get; set; }
        public string? LengthKm { get; set; }
        public string? RouteType { get; set; }
        public string? Surface { get; set; }
        public string? RaceCategory { get; set; }
        public string? Characteristic { get; set; }
        public string? Difficulty { get; set; }
        public string? Elevation { get; set; }
        public string? TurnCount { get; set; }
        public string? RoadWidth { get; set; }
        public string? AvgTimeSec { get; set; }
        public string? WorldRecord { get; set; }
        public string? WrVehicle { get; set; }
        public string? WrTuning { get; set; }
        public string? CarClassBest { get; set; }
        public string? CarTypeBest { get; set; }
        public string? Region { get; set; }
        public string? SeasonBest { get; set; }
        public string? WeatherDefault { get; set; }
        public string? NightMode { get; set; }
        public string? Dlc { get; set; }
        public string? IsReverse { get; set; }
        public string? ImageUrl { get; set; }
        public string? MinimapUrl { get; set; }
        public string? Notes { get; set; }
    }

    /// <summary>
    /// 车型请求模型
    /// </summary>
    public class VehicleModelRequest
    {
        public int? Id { get; set; }
        public string? Manufacturer { get; set; }
        public string? Model { get; set; }
        public string? Year { get; set; }
    }

    /// <summary>
    /// 调校请求模型（含车型字段以支持同步）
    /// </summary>
    public class VehicleTuneRequest
    {
        public int? Id { get; set; }
        public int? VehicleId { get; set; }
        public string? Manufacturer { get; set; }
        public string? Model { get; set; }
        public string? Year { get; set; }
        public string? Class { get; set; }
        public string? MatchType { get; set; }
        public string? Drivetrain { get; set; }
        public string? PerformancePreference { get; set; }
        public string? TireCompound { get; set; }
        public string? SurfaceCondition { get; set; }
        public string? Strength { get; set; }
        public string? TuneRaw { get; set; }
        public string? Remarks { get; set; }
        public string? LapTime { get; set; }
        public string? ExtData { get; set; }
        public string? SourceFileId { get; set; }
        public string? SourceSheet { get; set; }
        public int? SourceRow { get; set; }
        public string? SourceDisplayName { get; set; }
        public bool IsManual { get; set; }
        public string? TuneStatus { get; set; }
        public long LastUpdatedUserId { get; set; }
    }
}
