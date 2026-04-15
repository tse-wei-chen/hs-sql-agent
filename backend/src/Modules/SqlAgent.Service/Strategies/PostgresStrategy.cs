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

    protected override DbConnection CreateConnection(string? connectionString) => new NpgsqlConnection(connectionString);
    protected override Compiler CreateCompiler() => new PostgresCompiler();

    public override async Task<List<string>> GetSchemasAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            return [.. await connection.QueryAsync<string>("SELECT schema_name FROM information_schema.schemata;")];
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
            using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            return [.. await connection.QueryAsync<string>("SELECT table_name FROM information_schema.tables WHERE table_schema = @schemaName;", new { schemaName })];
        }
        catch (Exception ex)
        {
            throw new Exception(@$"
				Error getting tables: {ex.Message},
				please try again !!
			");
        }
    }

    public override async Task<List<string>> GetColumnsAsync(string connectionString, string tableName, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            return [.. await connection.QueryAsync<string>("SELECT column_name FROM information_schema.columns WHERE table_name = @tableName;", new { tableName })];
        }
        catch (Exception ex)
        {
            throw new Exception(@$"
				Error getting columns: {ex.Message},
				please try again !!
			");
        }
    }

    public override async Task<string> GetTableReferenceAsync(string connectionString, string tableName, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            const string sql = """
            SELECT
                kcu.table_name AS sourcetable,
                kcu.column_name AS foreignkey,
                rel_kcu.table_name AS referencetable,
                rel_kcu.column_name AS primarykey
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
                ON tc.constraint_name = kcu.constraint_name
                AND tc.table_schema = kcu.table_schema
            JOIN information_schema.referential_constraints rc
                ON tc.constraint_name = rc.constraint_name
            JOIN information_schema.key_column_usage rel_kcu
                ON rc.unique_constraint_name = rel_kcu.constraint_name
                AND rc.unique_constraint_schema = rel_kcu.table_schema
                AND kcu.ordinal_position = rel_kcu.ordinal_position
            WHERE tc.constraint_type = 'FOREIGN KEY'
                AND tc.table_name = @tableName
                AND tc.table_schema = 'public'
            ORDER BY rel_kcu.table_name, kcu.column_name;
            """;

            var references = await connection.QueryAsync<TableRefModel>(sql, new { tableName });

            return JsonSerializer.Serialize(references);

        }
        catch (Exception ex)
        {
            throw new Exception($"Error getting table reference: {ex.Message}", ex);
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
