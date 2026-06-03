namespace CandyCottage.CandyBot.Utilities;

public static class ChinaStandardTime
{
    public static DateTime Now => FromUtc(DateTime.UtcNow);

    public static DateTime FromUtc(DateTime value)
    {
        DateTime utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
        return DateTime.SpecifyKind(utc.AddHours(8), DateTimeKind.Unspecified);
    }
}
