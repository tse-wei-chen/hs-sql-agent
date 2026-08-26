using System.Globalization;
using System.Text.Json;
using SqlAgent.Service.Interfaces;

namespace SqlAgent.Service.Services;

public class QueryValueParserService : IQueryValueParserService
{
    public object UnwrapJsonElement(JsonElement je)
    {
        return je.ValueKind switch
        {
            JsonValueKind.String => (object)je.GetString()!,
            JsonValueKind.Number when je.TryGetInt64(out var l) => l,
            JsonValueKind.Number => je.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => je.EnumerateArray().Select(UnwrapJsonElement).ToArray(),
            _ => (object)je.ToString(),
        };
    }

    public bool TryToDateTime(object? value, out DateTime dateTime)
    {
        dateTime = default;
        if (value is null) return false;

        if (value is DateTime dt)
        {
            dateTime = dt;
            return true;
        }

        if (value is SqlDateValue date)
        {
            dateTime = date.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            return true;
        }

        if (value is SqlLocalDateTimeValue localTimestamp)
        {
            dateTime = DateTime.SpecifyKind(localTimestamp.Value, DateTimeKind.Unspecified);
            return true;
        }

        if (value is SqlOffsetDateTimeValue offsetTimestamp)
        {
            dateTime = offsetTimestamp.Value.UtcDateTime;
            return true;
        }

        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (text is null) return false;
        if (SqlTemporalLiteralParser.TryParseDate(text, out date))
        {
            dateTime = date.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            return true;
        }
        if (SqlTemporalLiteralParser.TryParseTimestamp(text, out var timestamp))
        {
            dateTime = timestamp switch
            {
                SqlLocalDateTimeValue local => DateTime.SpecifyKind(local.Value, DateTimeKind.Unspecified),
                SqlOffsetDateTimeValue offset => offset.Value.UtcDateTime,
                _ => default
            };
            return true;
        }
        return false;
    }

    public bool TryGetInValues(object? value, out IEnumerable<object> values)
    {
        values = [];

        if (value is null) return false;

        if (value is JsonElement je && je.ValueKind == JsonValueKind.Array)
        {
            values = [.. je.EnumerateArray().Select(UnwrapJsonElement).Cast<object>()];
            return values.Any();
        }

        if (value is JsonElement jeText && jeText.ValueKind == JsonValueKind.String)
            return TryGetInValues(jeText.GetString(), out values);

        if (value is IEnumerable<object> objEnum)
        {
            var arr = objEnum.Where(v => v is not null).ToArray();
            if (arr.Length == 0) return false;
            values = arr;
            return true;
        }

        if (value is string str)
        {
            var trimmed = str.Trim();
            if (trimmed.StartsWith('(') && trimmed.EndsWith(')')) trimmed = trimmed[1..^1];
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) trimmed = trimmed[1..^1];

            var parts = trimmed
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => p.Trim('\'', '"'))
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => TryToDateTime(p, out var dt) ? (object)dt : p)
                .ToArray();

            if (parts.Length == 0) return false;
            values = parts;
            return true;
        }

        return false;
    }

    public bool TryGetRangeValues(object? value, out object? start, out object? end)
    {
        start = null;
        end = null;

        if (value is null) return false;

        if (value is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Object)
            {
                if (je.TryGetProperty("start", out var startProp) && je.TryGetProperty("end", out var endProp))
                {
                    start = UnwrapJsonElement(startProp);
                    end = UnwrapJsonElement(endProp);
                }
            }
            else if (je.ValueKind == JsonValueKind.Array && je.GetArrayLength() >= 2)
            {
                start = UnwrapJsonElement(je[0]);
                end = UnwrapJsonElement(je[1]);
            }
        }
        else if (value is IEnumerable<object> list)
        {
            var arr = list.ToArray();
            if (arr.Length >= 2)
            {
                start = arr[0];
                end = arr[1];
            }
        }

        if (start != null && end != null)
        {
            if (start is string startStr && TryToDateTime(startStr, out var d1)) start = d1;
            if (end is string endStr && TryToDateTime(endStr, out var d2)) end = d2;
            return true;
        }

        return false;
    }

    private static object ConvertLiteral(string value)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longVal))
            return longVal;

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleVal))
            return doubleVal;

        if (bool.TryParse(value, out var boolVal))
            return boolVal;

        return value.Trim().Trim('\'', '"');
    }
}
