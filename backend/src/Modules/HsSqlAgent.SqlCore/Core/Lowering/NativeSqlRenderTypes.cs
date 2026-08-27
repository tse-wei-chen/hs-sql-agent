using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace HsSqlAgent.SqlCore.Core.Lowering;

/// <summary>
/// Provider-neutral rendered SQL fragment. Runtime values stay out of SQL text and remain ordered
/// positional bindings until the final command boundary assigns provider parameter names.
/// </summary>
internal sealed record NativeSqlFragment(
    string Sql,
    ImmutableArray<object?> Bindings)
{
    public static NativeSqlFragment Empty { get; } =
        new(string.Empty, ImmutableArray<object?>.Empty);
}

internal static class NativeSqlParameterizer
{
    // A control character cannot be produced by any accepted SQL identifier, operator, function,
    // or escaped literal. Keeping the internal marker out of SQL syntax avoids accidental
    // replacement of user text containing '?' while fragments are composed.
    public const string Placeholder = "\u001F";

    public static (
        string Sql,
        ImmutableArray<SqlParameterValue> Parameters) Finalize(
        NativeSqlFragment fragment,
        SqlAgentToolType provider)
    {
        ArgumentNullException.ThrowIfNull(fragment);

        var sql = new StringBuilder(fragment.Sql.Length + fragment.Bindings.Length * 4);
        var parameters = ImmutableArray.CreateBuilder<SqlParameterValue>(fragment.Bindings.Length);
        var bindingIndex = 0;
        var prefix = provider == SqlAgentToolType.Oracle ? ":p" : "@p";

        foreach (var ch in fragment.Sql)
        {
            if (ch != Placeholder[0])
            {
                sql.Append(ch);
                continue;
            }

            if (bindingIndex >= fragment.Bindings.Length)
            {
                throw new SqlCompilationException(
                    "Native SQL renderer produced more parameter markers than bindings.");
            }

            var name = prefix + bindingIndex;
            sql.Append(name);
            parameters.Add(new SqlParameterValue(name, fragment.Bindings[bindingIndex]));
            bindingIndex++;
        }

        if (bindingIndex != fragment.Bindings.Length)
        {
            throw new SqlCompilationException(
                "Native SQL renderer produced more bindings than parameter markers.");
        }

        return (sql.ToString(), parameters.ToImmutable());
    }
}

internal static class NativeSqlValueNormalizer
{
    public static object? Normalize(object? value)
    {
        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number when element.TryGetInt32(out var i32) => i32,
                JsonValueKind.Number when element.TryGetInt64(out var i64) => i64,
                JsonValueKind.Number when element.TryGetDecimal(out var dec) => dec,
                JsonValueKind.Number => element.GetDouble(),
                _ => throw new SqlCompilationException(
                    "JSON value kind " + element.ValueKind +
                    " cannot be bound as a scalar SQL parameter.")
            };
        }

        return value switch
        {
            SqlDateValue date => date.Value.ToDateTime(TimeOnly.MinValue),
            SqlTimeValue time => time.Value.ToTimeSpan(),
            SqlLocalDateTimeValue local =>
                DateTime.SpecifyKind(local.Value, DateTimeKind.Unspecified),
            SqlOffsetDateTimeValue offset => offset.Value,
            DateOnly date => date.ToDateTime(TimeOnly.MinValue),
            TimeOnly time => time.ToTimeSpan(),
            DateTime dateTime when dateTime.Kind != DateTimeKind.Unspecified =>
                DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified),
            _ => value
        };
    }
}
