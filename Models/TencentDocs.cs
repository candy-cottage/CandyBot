using System.Text.Json.Serialization;

namespace CandyCottage.CandyBot.Models
{
    public class SheetInfoResponse
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }
        [JsonPropertyName("message")]
        public string Message { get; set; } = "";
        [JsonPropertyName("properties")]
        public List<SheetProperty>? Properties { get; set; }
    }

    public class SheetProperty
    {
        [JsonPropertyName("sheetId")]
        public string? SheetId { get; set; }
        [JsonPropertyName("title")]
        public string? Title { get; set; }
        [JsonPropertyName("rowCount")]
        public int? RowCount { get; set; }
        [JsonPropertyName("columnCount")]
        public int? ColumnCount { get; set; }
    }

    public class SheetRangeDataResponse
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }
        [JsonPropertyName("message")]
        public string Message { get; set; } = "";
        [JsonPropertyName("gridData")]
        public GridData? GridData { get; set; }
    }

    public class GridData
    {
        [JsonPropertyName("rows")]
        public List<RowData>? Rows { get; set; }
    }

    public class RowData
    {
        [JsonPropertyName("values")]
        public List<CellData>? Values { get; set; }
    }

    public class CellData
    {
        [JsonPropertyName("cellValue")]
        public CellValue? CellValue { get; set; }
        [JsonPropertyName("dataType")]
        public string? DataType { get; set; }
    }

    public class CellValue
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }
        [JsonPropertyName("number")]
        public double? Number { get; set; }
        [JsonPropertyName("link")]
        public Link? Link { get; set; }
        [JsonPropertyName("time")]
        public Time? Time { get; set; }
        [JsonPropertyName("location")]
        public Location? Location { get; set; }
        [JsonPropertyName("select")]
        public Select? Select { get; set; }
    }

    public class Link
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }
        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }

    public class Time
    {
        [JsonPropertyName("year")]
        public int Year { get; set; }
        [JsonPropertyName("month")]
        public int Month { get; set; }
        [JsonPropertyName("day")]
        public int Day { get; set; }
        [JsonPropertyName("hour")]
        public int Hour { get; set; }
        [JsonPropertyName("minute")]
        public int Minute { get; set; }
        [JsonPropertyName("second")]
        public int Second { get; set; }
    }

    public class Location
    {
        [JsonPropertyName("longitude")]
        public string? Longitude { get; set; }
        [JsonPropertyName("latitude")]
        public string? Latitude { get; set; }
        [JsonPropertyName("name")]
        public string? Name { get; set; }
        [JsonPropertyName("uid")]
        public string? Uid { get; set; }
        [JsonPropertyName("source")]
        public string? Source { get; set; }
    }

    public class Select
    {
        [JsonPropertyName("multiple")]
        public bool Multiple { get; set; }
        [JsonPropertyName("value")]
        public string[]? Value { get; set; }
        [JsonPropertyName("options")]
        public Option[]? Options { get; set; }
    }

    public class Option
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
        [JsonPropertyName("text")]
        public string? Text { get; set; }
        [JsonPropertyName("color")]
        public Color? Color { get; set; }
    }

    public class Color
    {
        [JsonPropertyName("red")]
        public byte Red { get; set; }
        [JsonPropertyName("green")]
        public byte Green { get; set; }
        [JsonPropertyName("blue")]
        public byte Blue { get; set; }
        [JsonPropertyName("alpha")]
        public byte Alpha { get; set; }
    }
}
