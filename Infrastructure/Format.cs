using System.Globalization;

namespace CmxDialer.Infrastructure;

public static class Format
{
    public static string Phone(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.Length == 11 && digits[0] == '1') digits = digits[1..];
        if (digits.Length == 10)
            return $"({digits[..3]}) {digits.Substring(3, 3)}-{digits[6..]}";
        return raw.Trim();
    }

    public static string Digits(string? raw) =>
        new string((raw ?? "").Where(c => char.IsDigit(c) || c == '+').ToArray());

    public static string Clock(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    public static string LongClock(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }

    public static string Seconds(int? seconds) =>
        seconds is null ? "" : Clock(TimeSpan.FromSeconds(seconds.Value));

    public static string TimeOfDay(DateTimeOffset? value) =>
        value is null ? "" : value.Value.ToLocalTime().ToString("h:mm tt", CultureInfo.CurrentCulture);
}
