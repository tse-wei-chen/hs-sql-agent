using Dapper;
using Npgsql;
using SqlKata.Compilers;
using System.Data.Common;
using System.Text.Json;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Models;
using Microsoft.Extensions.Configuration;
using System.Text.RegularExpressions;

namespace SqlAgent.Service.Strategies;

public class PostgresStrategy(IQueryValueParserService valueParser, IConfiguration configuration) : BaseSqlStrategy(valueParser, configuration)
{
    public override SqlAgentToolType DbType => SqlAgentToolType.Postgres;

    public override DbConnection CreateConnection(string? connectionString) => new NpgsqlConnection(connectionString);
    protected override Compiler CreateCompiler() => new PostgresCompiler();

    public override async Task<List<string>> GetSchemasAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = CreateConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            return [.. await connection.QueryAsync<string>(new CommandDefinition("SELECT schema_name FROM information_schema.schemata;", cancellationToken: cancellationToken))];
        }
        catch (Exception ex)
        {
            throw new Exception(@$"
				Error getting schemas: {ex.Message},
				please try again !!
			");
        }
    }

    public override async Task<List<string>> GetTablesAsync(string connectionString, string schemaName, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = CreateConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            return [.. await connection.QueryAsync<string>(new CommandDefinition("SELECT table_name FROM information_schema.tables WHERE table_schema = @schemaName;", new { schemaName }, cancellationToken: cancellationToken))];
        }
        catch (Exception ex)
        {
            throw new Exception(@$"
				Error getting tables: {ex.Message},
				please try again !!
			");
        }
    }

    public override async Task<List<string>> GetColumnsAsync(string connectionString, string schemaName, string tableName, CancellationToken cancellationToken = default)
    {
        const string sql = @"
            SELECT column_name 
            FROM information_schema.columns 
            WHERE table_schema = @schemaName 
            AND table_name = @tableName
            ORDER BY ordinal_position;";

        try
        {
            using var connection = CreateConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            var columns = await connection.QueryAsync<string>(new CommandDefinition(sql, new { schemaName, tableName }, cancellationToken: cancellationToken));

            return [.. columns];
        }
        catch (Exception ex)
        {
            throw new Exception(@$"
				Error getting columns: {ex.Message},
				please try again !!
			");
        }
    }

    protected override string BuildExecutionErrorMessage(Exception ex, string type)
    {
        var code = ex is PostgresException pgEx ? pgEx.SqlState : TryExtractSqlStateCode(ex.Message);
        var hint = BuildHint(code, ex.Message);

        return $"Error executing query | code={code ?? "unknown"} | hint={hint}";
    }

    protected override string BuildHint(string? code, string message)
    {
        if (string.Equals(code, "42883", StringComparison.OrdinalIgnoreCase))
        {
            if (message.Contains("date", StringComparison.OrdinalIgnoreCase) &&
                message.Contains("text", StringComparison.OrdinalIgnoreCase))
            {
                return "Date vs Text type mismatch. Fix: Set 'IsDate': true in WhereCondition/HavingCondition, or ensure literals are in ISO format.";
            }

            if (message.Contains("in", StringComparison.OrdinalIgnoreCase))
            {
                return "Operator mismatch for IN/NOT IN. Fix: Use the 'Values' list (for constants) or 'SubQuery' (for dynamic sets) instead of the 'Value' field.";
            }

            return "Operator/type mismatch. Ensure 'Value' matches the field type. For calculations, use the 'Arithmetic' object instead of raw strings.";
        }

        if (string.Equals(code, "42703", StringComparison.OrdinalIgnoreCase))
        {
            return "Column not recognized. Tips: 1. Ensure 'Field' name is correct. 2. For SQL functions or complex logic, use 'Aggregation', 'Arithmetic', or 'CaseWhen' instead of raw strings in 'Field'.";
        }

        if (string.Equals(code, "42P01", StringComparison.OrdinalIgnoreCase))
        {
            return "Table or CTE not found. Check 'TableName'. For CTEs, use the 'CteConditions' list and refer to them by their 'Name' (unqualified).";
        }

        if (string.Equals(code, "42702", StringComparison.OrdinalIgnoreCase))
        {
            return "Ambiguous column. Fix: Use 'TableName.FieldName' in the 'Field' property to qualify which table the column belongs to.";
        }

        if (string.Equals(code, "22P02", StringComparison.OrdinalIgnoreCase))
        {
            return "Invalid value format. Ensure the 'Value' (or 'Constant' in Arithmetic) matches the database column type (e.g., UUID, Integer, or Timestamp).";
        }

        if (string.Equals(code, "42601", StringComparison.OrdinalIgnoreCase))
        {
            return "Syntax error. Check if 'SubQuery' is missing a 'TableName', or if 'Arithmetic' operators (+, -, *, /) are used correctly.";
        }

        return base.BuildHint(code, message);
    }

    private static string? TryExtractSqlStateCode(string message)
    {
        var match = Regex.Match(message ?? string.Empty, @"\b(?<code>[0-9A-Z]{5})\b");
        if (!match.Success) return null;

        var code = match.Groups["code"].Value;
        return code.Any(char.IsDigit) ? code : null;
    }
}
