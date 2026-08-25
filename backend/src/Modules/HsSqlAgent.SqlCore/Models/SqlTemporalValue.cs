using System.Text.Json.Serialization;

namespace HsSqlAgent.SqlCore.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SqlDateValue), "date")]
[JsonDerivedType(typeof(SqlTimeValue), "time")]
[JsonDerivedType(typeof(SqlLocalDateTimeValue), "local_timestamp")]
[JsonDerivedType(typeof(SqlOffsetDateTimeValue), "offset_timestamp")]
public abstract class SqlTemporalValue;

/// <summary>
/// A calendar date without a time of day or time zone. This is the canonical
/// representation used between SQL parsing and provider parameter binding.
/// </summary>
public sealed class SqlDateValue : SqlTemporalValue
{
    public DateOnly Value { get; set; }

    public SqlDateValue() { }

    public SqlDateValue(DateOnly value)
    {
        Value = value;
    }
}

/// <summary>A time of day without a date or time zone.</summary>
public sealed class SqlTimeValue : SqlTemporalValue
{
    public TimeOnly Value { get; set; }

    public SqlTimeValue() { }
    public SqlTimeValue(TimeOnly value) => Value = value;
}

/// <summary>A date and time without a time zone or UTC offset.</summary>
public sealed class SqlLocalDateTimeValue : SqlTemporalValue
{
    public DateTime Value { get; set; }

    public SqlLocalDateTimeValue() { }
    public SqlLocalDateTimeValue(DateTime value) =>
        Value = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
}

/// <summary>A date and time with an explicit UTC offset.</summary>
public sealed class SqlOffsetDateTimeValue : SqlTemporalValue
{
    public DateTimeOffset Value { get; set; }

    public SqlOffsetDateTimeValue() { }
    public SqlOffsetDateTimeValue(DateTimeOffset value) => Value = value;
}
