using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HsSqlAgent.Server.Services;

internal static partial class CustomToolSqlTemplate
{
    internal sealed record Parameter(string Name, string Type, string? Description);

    [GeneratedRegex(@"\{\{\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    public static IReadOnlyList<Parameter> ParseParameters(string? parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson)) return [];
        using var document = JsonDocument.Parse(parametersJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("ParametersJson must be a JSON array.");

        var result = new List<Parameter>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString()?.Trim() : null;
            var type = item.TryGetProperty("type", out var typeElement) ? typeElement.GetString()?.Trim().ToLowerInvariant() : "string";
            var description = item.TryGetProperty("description", out var descriptionElement) ? descriptionElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(name) || !Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$"))
                throw new InvalidOperationException("Parameter names must use letters, numbers, and underscores and cannot start with a number.");
            if (!names.Add(name)) throw new InvalidOperationException($"Duplicate parameter '{name}'.");
            if (type is not ("string" or "number" or "boolean"))
                throw new InvalidOperationException($"Unsupported type '{type}' for parameter '{name}'.");
            result.Add(new Parameter(name, type, description));
        }
        return result;
    }

    public static string RenderForValidation(string sqlTemplate, string? parametersJson)
    {
        var parameters = ParseParameters(parametersJson);
        var values = parameters.ToDictionary<Parameter, string, object?>(
            x => x.Name,
            x => x.Type switch
            {
                "number" => (object)0,
                "boolean" => false,
                _ => "sample"
            },
            StringComparer.OrdinalIgnoreCase);
        return Render(sqlTemplate, parameters, values);
    }

    public static string Render(
        string sqlTemplate,
        string? parametersJson,
        IReadOnlyDictionary<string, object?> values)
        => Render(sqlTemplate, ParseParameters(parametersJson), values);

    private static string Render(
        string sqlTemplate,
        IReadOnlyList<Parameter> parameters,
        IReadOnlyDictionary<string, object?> values)
    {
        if (string.IsNullOrWhiteSpace(sqlTemplate))
            throw new InvalidOperationException("SQL template must not be empty.");

        var definitions = parameters.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rendered = PlaceholderRegex().Replace(sqlTemplate, match =>
        {
            if (IsInsideQuotedSqlToken(sqlTemplate, match.Index))
                throw new InvalidOperationException("Placeholders must be unquoted SQL value tokens.");
            var name = match.Groups["name"].Value;
            if (!definitions.TryGetValue(name, out var parameter))
                throw new InvalidOperationException($"Placeholder '{name}' is not declared.");
            if (!TryGetValue(values, name, out var value))
                throw new InvalidOperationException($"A value is required for parameter '{name}'.");
            used.Add(name);
            return ToSqlLiteral(parameter, value);
        });

        var unused = definitions.Keys.Where(x => !used.Contains(x)).ToArray();
        if (unused.Length > 0)
            throw new InvalidOperationException($"Declared parameter(s) are not used: {string.Join(", ", unused)}.");
        if (rendered.Contains("{{", StringComparison.Ordinal) || rendered.Contains("}}", StringComparison.Ordinal))
            throw new InvalidOperationException("SQL template contains an invalid placeholder.");
        return rendered;
    }

    private static bool TryGetValue(IReadOnlyDictionary<string, object?> values, string name, out object? value)
    {
        foreach (var pair in values)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }
        value = null;
        return false;
    }

    private static string ToSqlLiteral(Parameter parameter, object? value)
    {
        if (value is null) return "NULL";
        var text = value is JsonElement element
            ? element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => null,
                _ => throw new InvalidOperationException($"Parameter '{parameter.Name}' must be a scalar value.")
            }
            : Convert.ToString(value, CultureInfo.InvariantCulture);
        if (text is null) return "NULL";

        return parameter.Type switch
        {
            "string" => $"'{text.Replace("'", "''", StringComparison.Ordinal)}'",
            "number" when decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _) => text,
            "number" => throw new InvalidOperationException($"Parameter '{parameter.Name}' must be a number."),
            "boolean" when bool.TryParse(text, out var boolean) => boolean ? "TRUE" : "FALSE",
            "boolean" => throw new InvalidOperationException($"Parameter '{parameter.Name}' must be a boolean."),
            _ => throw new InvalidOperationException($"Unsupported parameter type '{parameter.Type}'.")
        };
    }

    private static bool IsInsideQuotedSqlToken(string sql, int position)
    {
        char? quote = null;
        var inBracketIdentifier = false;
        var inLineComment = false;
        var inBlockComment = false;
        for (var i = 0; i < position; i++)
        {
            var current = sql[i];
            if (inLineComment)
            {
                if (current is '\r' or '\n') inLineComment = false;
                continue;
            }
            if (inBlockComment)
            {
                if (current == '*' && i + 1 < position && sql[i + 1] == '/')
                {
                    inBlockComment = false;
                    i++;
                }
                continue;
            }
            if (inBracketIdentifier)
            {
                if (current != ']') continue;
                if (i + 1 < position && sql[i + 1] == ']') i++;
                else inBracketIdentifier = false;
                continue;
            }
            if (quote.HasValue)
            {
                if (current != quote.Value) continue;
                if (i + 1 < position && sql[i + 1] == quote.Value) i++;
                else quote = null;
                continue;
            }
            if (current == '-' && i + 1 < position && sql[i + 1] == '-')
            {
                inLineComment = true;
                i++;
            }
            else if (current == '/' && i + 1 < position && sql[i + 1] == '*')
            {
                inBlockComment = true;
                i++;
            }
            else if (current == '[') inBracketIdentifier = true;
            else if (current is '\'' or '"' or '`') quote = current;
        }
        return quote.HasValue || inBracketIdentifier || inLineComment || inBlockComment;
    }
}
