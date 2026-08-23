using System.Globalization;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.SqlParsing;

internal static class SqlTemporalLiteralParser
{
    private static readonly string[] TimeFormats =
    [
        "HH:mm",
        "HH:mm:ss",
        "HH:mm:ss.FFFFFFF"
    ];

    private static readonly string[] LocalTimestampFormats =
    [
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mm",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"
    ];

    private static readonly string[] OffsetTimestampFormats =
    [
        "yyyy-MM-dd HH:mmzzz",
        "yyyy-MM-dd HH:mm:sszzz",
        "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz",
        "yyyy-MM-dd'T'HH:mmzzz",
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"
    ];

    public static bool TryParseDate(string value, out SqlDateValue date)
    {
        if (DateOnly.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            date = new SqlDateValue(parsed);
            return true;
        }

        date = null!;
        return false;
    }

    public static bool TryParseTime(string value, out SqlTimeValue time)
    {
        if (TimeOnly.TryParseExact(
                value,
                TimeFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            time = new SqlTimeValue(parsed);
            return true;
        }

        time = null!;
        return false;
    }

    public static bool TryParseTimestamp(string value, out SqlTemporalValue timestamp)
    {
        if (value.EndsWith('Z')
            && DateTime.TryParseExact(
                value[..^1],
                LocalTimestampFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var utcTimestamp))
        {
            timestamp = new SqlOffsetDateTimeValue(
                new DateTimeOffset(DateTime.SpecifyKind(utcTimestamp, DateTimeKind.Unspecified), TimeSpan.Zero));
            return true;
        }

        if (HasExplicitOffset(value)
            && DateTimeOffset.TryParseExact(
                value,
                OffsetTimestampFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var offsetTimestamp))
        {
            timestamp = new SqlOffsetDateTimeValue(offsetTimestamp);
            return true;
        }

        if (DateTime.TryParseExact(
                value,
                LocalTimestampFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var localTimestamp))
        {
            timestamp = new SqlLocalDateTimeValue(localTimestamp);
            return true;
        }

        timestamp = null!;
        return false;
    }

    private static bool HasExplicitOffset(string value)
    {
        if (value.EndsWith('Z')) return true;
        var timeSeparator = Math.Max(value.LastIndexOf('T'), value.LastIndexOf(' '));
        if (timeSeparator < 0) return false;
        return value.LastIndexOf('+') > timeSeparator || value.LastIndexOf('-') > timeSeparator;
    }
}
