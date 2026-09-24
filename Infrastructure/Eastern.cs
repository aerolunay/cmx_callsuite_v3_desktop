namespace CmxDialer.Infrastructure;

/// <summary>
/// Every time the app shows or sends is US Eastern (the server's and database's zone),
/// no matter where the agent's PC is — Philippines, Dominican Republic or the US.
/// Eastern follows US daylight saving: EST in winter, EDT in summer.
/// </summary>
public static class Eastern
{
    public static readonly TimeZoneInfo Zone = Find();

    private static TimeZoneInfo Find()
    {
        foreach (var id in new[] { "Eastern Standard Time", "America/New_York" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        Log.Error("US Eastern time zone not found on this PC — falling back to local time");
        return TimeZoneInfo.Local;
    }

    public static DateTime Now => TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.Utc, Zone);
    public static DateTime Today => Now.Date;

    public static DateTime From(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, Zone).DateTime;

    /// <summary>"EST" or "EDT" for the given Eastern wall-clock time.</summary>
    public static string Abbreviation(DateTime eastern) => Zone.IsDaylightSavingTime(eastern) ? "EDT" : "EST";
}
