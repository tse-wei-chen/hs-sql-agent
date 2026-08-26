using HsSqlAgent.SqlCore.Enums;

namespace HsSqlAgent.SqlCore.SqlTranslation.DateFormats;

public abstract record DateFormatPart;
public sealed record DateFormatLiteral(string Value) : DateFormatPart;
public sealed record DateFormatToken(DateFormatTokenKind Kind) : DateFormatPart;

public enum DateFormatTokenKind
{
    Year4,
    Year2,
    Month2,
    MonthName,
    MonthShortName,
    Day2,
    DayNoPadding,
    Hour24,
    Hour12,
    Minute,
    Second,
    AmPm
}

public interface IDateFormatDialect
{
    IReadOnlyList<DateFormatPart> Parse(string value);
    string Render(IReadOnlyList<DateFormatPart> format);
}

public sealed class DateFormatTranslator
{
    private readonly IReadOnlyDictionary<SqlAgentToolType, IDateFormatDialect> _dialects;

    public DateFormatTranslator(IReadOnlyDictionary<SqlAgentToolType, IDateFormatDialect>? dialects = null)
    {
        _dialects = dialects ?? new Dictionary<SqlAgentToolType, IDateFormatDialect>
        {
            [SqlAgentToolType.Sqlite] = new PercentDateFormatDialect(SqlAgentToolType.Sqlite),
            [SqlAgentToolType.MySQL] = new PercentDateFormatDialect(SqlAgentToolType.MySQL),
            [SqlAgentToolType.MsSqlServer] = new NamedDateFormatDialect(SqlAgentToolType.MsSqlServer),
            [SqlAgentToolType.Postgres] = new NamedDateFormatDialect(SqlAgentToolType.Postgres),
            [SqlAgentToolType.Oracle] = new NamedDateFormatDialect(SqlAgentToolType.Oracle)
        };
    }

    public string Translate(string value, SqlAgentToolType source, SqlAgentToolType target)
    {
        return Render(Parse(value, source), target);
    }

    public IReadOnlyList<DateFormatPart> Parse(string value, SqlAgentToolType source) =>
        _dialects.TryGetValue(source, out var parser)
            ? parser.Parse(value)
            : throw new NotSupportedException($"Date format parsing is not supported for {source}.");

    public string Render(IReadOnlyList<DateFormatPart> format, SqlAgentToolType target) =>
        _dialects.TryGetValue(target, out var renderer)
            ? renderer.Render(format)
            : throw new NotSupportedException($"Date format rendering is not supported for {target}.");
}

internal sealed class PercentDateFormatDialect(SqlAgentToolType dialect) : IDateFormatDialect
{
    private readonly SqlAgentToolType _dialect = dialect;

    public IReadOnlyList<DateFormatPart> Parse(string value)
    {
        var result = new List<DateFormatPart>();
        var literal = new System.Text.StringBuilder();
        void FlushLiteral()
        {
            if (literal.Length == 0) return;
            result.Add(new DateFormatLiteral(literal.ToString()));
            literal.Clear();
        }

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '%' || i + 1 >= value.Length)
            {
                literal.Append(value[i]);
                continue;
            }

            var specifier = value[++i];
            if (specifier == '%')
            {
                literal.Append('%');
                continue;
            }

            var token = ParseToken(specifier);
            if (token is null)
                throw new FormatException(
                    $"Unsupported {_dialect} date-format token '%{specifier}'.");
            FlushLiteral();
            result.Add(new DateFormatToken(token.Value));
        }
        FlushLiteral();
        return result;
    }

    public string Render(IReadOnlyList<DateFormatPart> format) => string.Concat(format.Select(RenderPart));

    private DateFormatTokenKind? ParseToken(char token) => token switch
    {
        'Y' => DateFormatTokenKind.Year4,
        'y' => DateFormatTokenKind.Year2,
        'm' => DateFormatTokenKind.Month2,
        'b' => DateFormatTokenKind.MonthShortName,
        // This deliberate dialect split is why parsing must know its source dialect.
        'M' when _dialect == SqlAgentToolType.MySQL => DateFormatTokenKind.MonthName,
        'M' when _dialect == SqlAgentToolType.Sqlite => DateFormatTokenKind.Minute,
        'd' => DateFormatTokenKind.Day2,
        'e' => DateFormatTokenKind.DayNoPadding,
        'H' => DateFormatTokenKind.Hour24,
        'h' or 'I' => DateFormatTokenKind.Hour12,
        'i' when _dialect == SqlAgentToolType.MySQL => DateFormatTokenKind.Minute,
        'S' or 's' => DateFormatTokenKind.Second,
        'p' => DateFormatTokenKind.AmPm,
        _ => null
    };

    private string RenderPart(DateFormatPart part) => part switch
    {
        DateFormatLiteral literal => literal.Value.Replace("%", "%%"),
        DateFormatToken token => (_dialect, token.Kind) switch
        {
            (_, DateFormatTokenKind.Year4) => "%Y",
            (_, DateFormatTokenKind.Year2) => "%y",
            (_, DateFormatTokenKind.Month2) => "%m",
            (SqlAgentToolType.MySQL, DateFormatTokenKind.MonthName) => "%M",
            (_, DateFormatTokenKind.MonthName) => "%m",
            (SqlAgentToolType.MySQL, DateFormatTokenKind.MonthShortName) => "%b",
            (_, DateFormatTokenKind.MonthShortName) => "%m",
            (_, DateFormatTokenKind.Day2) => "%d",
            (SqlAgentToolType.MySQL, DateFormatTokenKind.DayNoPadding) => "%e",
            (_, DateFormatTokenKind.DayNoPadding) => "%d",
            (_, DateFormatTokenKind.Hour24) => "%H",
            (_, DateFormatTokenKind.Hour12) => "%I",
            (SqlAgentToolType.MySQL, DateFormatTokenKind.Minute) => "%i",
            (_, DateFormatTokenKind.Minute) => "%M",
            (_, DateFormatTokenKind.Second) => "%S",
            (_, DateFormatTokenKind.AmPm) => "%p",
            _ => throw new ArgumentOutOfRangeException(nameof(part))
        },
        _ => throw new ArgumentOutOfRangeException(nameof(part))
    };
}

internal sealed class NamedDateFormatDialect(SqlAgentToolType dialect) : IDateFormatDialect
{
    private readonly SqlAgentToolType _dialect = dialect;

    public IReadOnlyList<DateFormatPart> Parse(string value)
    {
        var tokens = SourceTokens();
        var result = new List<DateFormatPart>();
        var literal = new System.Text.StringBuilder();
        void FlushLiteral()
        {
            if (literal.Length == 0) return;
            result.Add(new DateFormatLiteral(literal.ToString()));
            literal.Clear();
        }

        for (var pos = 0; pos < value.Length;)
        {
            var match = tokens.FirstOrDefault(candidate =>
                value.AsSpan(pos).StartsWith(candidate.Text, StringComparison.Ordinal));
            if (match.Text is null)
            {
                if (char.IsLetter(value[pos]))
                    throw new FormatException(
                        $"Unsupported {_dialect} date-format token near '{value[pos..]}'.");
                literal.Append(value[pos++]);
                continue;
            }
            FlushLiteral();
            result.Add(new DateFormatToken(match.Kind));
            pos += match.Text.Length;
        }
        FlushLiteral();
        return result;
    }

    public string Render(IReadOnlyList<DateFormatPart> format) => string.Concat(format.Select(part => part switch
    {
        DateFormatLiteral literal => literal.Value,
        DateFormatToken token => RenderToken(token.Kind),
        _ => throw new ArgumentOutOfRangeException(nameof(part))
    }));

    private IReadOnlyList<(string Text, DateFormatTokenKind Kind)> SourceTokens() =>
        _dialect == SqlAgentToolType.MsSqlServer
            ?
            [
                ("yyyy", DateFormatTokenKind.Year4), ("yy", DateFormatTokenKind.Year2),
                ("MMMM", DateFormatTokenKind.MonthName), ("MMM", DateFormatTokenKind.MonthShortName),
                ("MM", DateFormatTokenKind.Month2), ("dd", DateFormatTokenKind.Day2), ("d", DateFormatTokenKind.DayNoPadding),
                ("HH", DateFormatTokenKind.Hour24), ("hh", DateFormatTokenKind.Hour12),
                ("mm", DateFormatTokenKind.Minute), ("ss", DateFormatTokenKind.Second), ("tt", DateFormatTokenKind.AmPm)
            ]
            :
            [
                ("YYYY", DateFormatTokenKind.Year4), ("HH24", DateFormatTokenKind.Hour24),
                ("HH12", DateFormatTokenKind.Hour12), ("MONTH", DateFormatTokenKind.MonthName),
                ("MON", DateFormatTokenKind.MonthShortName), ("YY", DateFormatTokenKind.Year2),
                ("MM", DateFormatTokenKind.Month2), ("FMDD", DateFormatTokenKind.DayNoPadding),
                ("DD", DateFormatTokenKind.Day2), ("MI", DateFormatTokenKind.Minute),
                ("SS", DateFormatTokenKind.Second), ("AM", DateFormatTokenKind.AmPm), ("PM", DateFormatTokenKind.AmPm)
            ];

    private string RenderToken(DateFormatTokenKind token) => (_dialect, token) switch
    {
        (SqlAgentToolType.MsSqlServer, DateFormatTokenKind.Year4) => "yyyy",
        (SqlAgentToolType.MsSqlServer, DateFormatTokenKind.Year2) => "yy",
        (SqlAgentToolType.MsSqlServer, DateFormatTokenKind.Month2) => "MM",
        (SqlAgentToolType.MsSqlServer, DateFormatTokenKind.MonthName) => "MMMM",
        (SqlAgentToolType.MsSqlServer, DateFormatTokenKind.MonthShortName) => "MMM",
        (SqlAgentToolType.MsSqlServer, DateFormatTokenKind.Day2) => "dd",
        (SqlAgentToolType.MsSqlServer, DateFormatTokenKind.DayNoPadding) => "d",
        (SqlAgentToolType.MsSqlServer, DateFormatTokenKind.Hour24) => "HH",
        (SqlAgentToolType.MsSqlServer, DateFormatTokenKind.Hour12) => "hh",
        (SqlAgentToolType.MsSqlServer, DateFormatTokenKind.Minute) => "mm",
        (SqlAgentToolType.MsSqlServer, DateFormatTokenKind.Second) => "ss",
        (SqlAgentToolType.MsSqlServer, DateFormatTokenKind.AmPm) => "tt",
        (_, DateFormatTokenKind.Year4) => "YYYY",
        (_, DateFormatTokenKind.Year2) => "YY",
        (_, DateFormatTokenKind.Month2) => "MM",
        (_, DateFormatTokenKind.MonthName) => "MONTH",
        (_, DateFormatTokenKind.MonthShortName) => "MON",
        (_, DateFormatTokenKind.Day2) => "DD",
        (_, DateFormatTokenKind.DayNoPadding) => "FMDD",
        (_, DateFormatTokenKind.Hour24) => "HH24",
        (_, DateFormatTokenKind.Hour12) => "HH12",
        (_, DateFormatTokenKind.Minute) => "MI",
        (_, DateFormatTokenKind.Second) => "SS",
        (_, DateFormatTokenKind.AmPm) => "AM",
        _ => throw new ArgumentOutOfRangeException(nameof(token))
    };
}
