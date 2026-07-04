using System.Text.RegularExpressions;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.SqlParsing;

public static partial class SqlDefinitionParser
{
    public static QueryDefinition ParseQuery(string sql)
    {
        RejectMultipleStatements(sql);
        var tokens = new SqlTokenizer(sql).Tokenize();
        return new SqlParser(tokens).Parse();
    }

    public static DmlDefinition ParseDml(string sql)
    {
        RejectMultipleStatements(sql);
        var normalized = TrimTrailingSemicolon(sql.Trim());

        if (IsInsertRegex().IsMatch(normalized))
            return ParseInsertSql(normalized);
        if (IsUpdateRegex().IsMatch(normalized))
            return ParseUpdateSql(normalized);
        if (IsDeleteRegex().IsMatch(normalized))
            return ParseDeleteSql(normalized);

        throw new SqlParseException("Expected INSERT, UPDATE, or DELETE DML statement.");
    }

    private static DmlDefinition ParseInsertSql(string sql)
    {
        var match = InsertRegex().Match(sql);
        if (!match.Success)
            throw new SqlParseException("Only INSERT INTO table (columns...) VALUES (...) is supported for execute_dml_sql.");

        var columns = SplitSqlList(match.Groups["columns"].Value).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        var values = SplitSqlList(match.Groups["values"].Value).Select(ParseSqlLiteral).ToList();

        if (columns.Count == 0)
            throw new SqlParseException("INSERT statements must include a column list.");
        if (columns.Count != values.Count)
            throw new SqlParseException("INSERT column count must match value count.");

        return new DmlDefinition
        {
            Operation = DmlOperation.Insert,
            TableName = match.Groups["table"].Value,
            Values = [.. columns.Select((column, i) => new NameValuePair
            {
                FieldName = column.Trim(),
                Value = values[i]
            })]
        };
    }

    private static DmlDefinition ParseUpdateSql(string sql)
    {
        var match = UpdateRegex().Match(sql);
        if (!match.Success)
            throw new SqlParseException("Only UPDATE table SET column = value [, ...] [WHERE ...] is supported for execute_dml_sql.");

        return new DmlDefinition
        {
            Operation = DmlOperation.Update,
            TableName = match.Groups["table"].Value,
            Values = [.. SplitSqlList(match.Groups["set"].Value).Select(ParseAssignment)],
            WhereConditions = ParseDmlWhere(match.Groups["where"].Value)
        };
    }

    private static DmlDefinition ParseDeleteSql(string sql)
    {
        var match = DeleteRegex().Match(sql);
        if (!match.Success)
            throw new SqlParseException("Only DELETE FROM table [WHERE ...] is supported for execute_dml_sql.");

        return new DmlDefinition
        {
            Operation = DmlOperation.Delete,
            TableName = match.Groups["table"].Value,
            WhereConditions = ParseDmlWhere(match.Groups["where"].Value)
        };
    }

    private static NameValuePair ParseAssignment(string assignment)
    {
        var parts = SplitFirstOperator(assignment, "=");
        return parts == null
            ? throw new SqlParseException($"Invalid assignment: {assignment}")
            : new NameValuePair
            {
                FieldName = parts.Value.Left.Trim(),
                Value = ParseSqlLiteral(parts.Value.Right.Trim())
            };
    }

    private static List<WhereCondition>? ParseDmlWhere(string where)
    {
        if (string.IsNullOrWhiteSpace(where))
            return null;

        var conditions = SplitSqlKeyword(where, "AND")
            .Select(ParseBasicWhereCondition)
            .ToList<WhereCondition>();

        return conditions.Count == 0 ? null : conditions;
    }

    private static BasicWhereCondition ParseBasicWhereCondition(string condition)
    {
        foreach (var op in new[] { ">=", "<=", "<>", "!=", "=", ">", "<" })
        {
            var parts = SplitFirstOperator(condition, op);
            if (parts == null) continue;
            return new BasicWhereCondition
            {
                FieldName = parts.Value.Left.Trim(),
                Operator = op,
                Value = ParseSqlLiteral(parts.Value.Right.Trim())
            };
        }

        throw new SqlParseException($"Unsupported WHERE condition: {condition}");
    }

    private static (string Left, string Right)? SplitFirstOperator(string input, string op)
    {
        var index = IndexOfTopLevel(input, op);
        if (index < 0) return null;
        return (input[..index], input[(index + op.Length)..]);
    }

    private static List<string> SplitSqlList(string input)
    {
        var result = new List<string>();
        var start = 0;
        var depth = 0;
        var inString = false;
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (c == '\'' && (i + 1 >= input.Length || input[i + 1] != '\''))
                inString = !inString;
            else if (c == '\'' && i + 1 < input.Length && input[i + 1] == '\'')
                i++;
            else if (!inString && c == '(')
                depth++;
            else if (!inString && c == ')')
                depth--;
            else if (!inString && depth == 0 && c == ',')
            {
                result.Add(input[start..i].Trim());
                start = i + 1;
            }
        }
        result.Add(input[start..].Trim());
        return result;
    }

    private static List<string> SplitSqlKeyword(string input, string keyword)
    {
        var parts = new List<string>();
        var start = 0;
        var inString = false;
        for (var i = 0; i <= input.Length - keyword.Length; i++)
        {
            var c = input[i];
            if (c == '\'' && (i + 1 >= input.Length || input[i + 1] != '\''))
                inString = !inString;
            else if (c == '\'' && i + 1 < input.Length && input[i + 1] == '\'')
                i++;

            if (inString || !input.AsSpan(i, keyword.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase))
                continue;

            var beforeOk = i == 0 || char.IsWhiteSpace(input[i - 1]);
            var afterOk = i + keyword.Length >= input.Length || char.IsWhiteSpace(input[i + keyword.Length]);
            if (!beforeOk || !afterOk) continue;
            parts.Add(input[start..i].Trim());
            start = i + keyword.Length;
        }
        parts.Add(input[start..].Trim());
        return [.. parts.Where(p => !string.IsNullOrWhiteSpace(p))];
    }

    private static int IndexOfTopLevel(string input, string value)
    {
        var depth = 0;
        var inString = false;
        for (var i = 0; i <= input.Length - value.Length; i++)
        {
            var c = input[i];
            if (c == '\'' && (i + 1 >= input.Length || input[i + 1] != '\''))
                inString = !inString;
            else if (c == '\'' && i + 1 < input.Length && input[i + 1] == '\'')
                i++;
            else if (!inString && c == '(')
                depth++;
            else if (!inString && c == ')')
                depth--;

            if (!inString && depth == 0 && input.AsSpan(i, value.Length).Equals(value, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    private static object? ParseSqlLiteral(string value)
    {
        value = value.Trim();
        if (value.Equals("null", StringComparison.OrdinalIgnoreCase)) return null;
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (value.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            return value[1..^1].Replace("''", "'");
        if (int.TryParse(value, out var i)) return i;
        if (decimal.TryParse(value, out var d)) return d;
        return value;
    }

    private static void RejectMultipleStatements(string sql)
    {
        var trimmed = TrimTrailingSemicolon(sql.Trim());
        var inString = false;
        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (c == '\'' && (i + 1 >= trimmed.Length || trimmed[i + 1] != '\''))
                inString = !inString;
            else if (c == '\'' && i + 1 < trimmed.Length && trimmed[i + 1] == '\'')
                i++;
            else if (!inString && c == ';')
                throw new SqlParseException("Only one SQL statement is allowed.");
        }
    }

    private static string TrimTrailingSemicolon(string sql)
        => sql.EndsWith(';') ? sql[..^1].TrimEnd() : sql;
    [GeneratedRegex(@"^\s*insert\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IsInsertRegex();
    [GeneratedRegex(@"^\s*update\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IsUpdateRegex();
    [GeneratedRegex(@"^\s*delete\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IsDeleteRegex();
    [GeneratedRegex(@"^\s*insert\s+into\s+(?<table>[^\s(]+)\s*(?:\((?<columns>[^)]*)\))?\s+values\s*\((?<values>.*)\)\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex InsertRegex();
    [GeneratedRegex(@"^\s*update\s+(?<table>[^\s]+)\s+set\s+(?<set>.*?)(?:\s+where\s+(?<where>.*))?\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex UpdateRegex();
    [GeneratedRegex(@"^\s*delete\s+from\s+(?<table>[^\s]+)(?:\s+where\s+(?<where>.*))?\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex DeleteRegex();
}
