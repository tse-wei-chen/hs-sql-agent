using Dapper;
using Oracle.ManagedDataAccess.Client;
using SqlKata.Compilers;
using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Interfaces;

using Microsoft.Extensions.Configuration;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.Strategies;

public class OracleStrategy(IQueryValueParserService valueParser, IConfiguration configuration) : BaseSqlStrategy(valueParser, configuration)
{
    public override SqlAgentToolType DbType => SqlAgentToolType.Oracle;
    public override string BuildConnectionString(BuildDbConnectionModelBase model)
    {
        var builder = new OracleConnectionStringBuilder
        {
            DataSource = $"{model.Host}:{(string.IsNullOrEmpty(model.Port) ? "1521" : model.Port)}/{model.Database}",
            UserID = model.Username,
            Password = model.Password
        };
        return builder.ConnectionString;
    }
    public override DbConnection CreateConnection(string? connectionString) => new OracleConnection(connectionString);
    protected override Compiler CreateCompiler() => new OracleCompiler();

    public override async Task<List<string>> GetSchemasAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = CreateConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            return [.. await connection.QueryAsync<string>("SELECT USERNAME FROM ALL_USERS ORDER BY USERNAME")];
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
            var sql = "SELECT TABLE_NAME FROM ALL_TABLES WHERE OWNER = :schemaName ORDER BY TABLE_NAME";
            var tables = await connection.QueryAsync<string>(sql, new { schemaName = schemaName.ToUpper() });
            return [.. tables];
        }
        catch (Exception ex)
        {
            throw new Exception(@$"
				Error getting tables: {ex.Message},
				please try again !!
			");
        }
    }

    public override async Task<List<ColumnInfo>> GetColumnsAsync(string connectionString, string schemaName, string tableName, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = CreateConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            var sql = "SELECT COLUMN_NAME, DATA_TYPE FROM ALL_TAB_COLUMNS WHERE OWNER = :schemaName AND TABLE_NAME = :tableName ORDER BY COLUMN_ID";
            var rows = await connection.QueryAsync(sql, new
            {
                schemaName = schemaName.ToUpper(),
                tableName = tableName.ToUpper()
            });
            return [.. rows.Select(r => new ColumnInfo((string)r.COLUMN_NAME, (string)r.DATA_TYPE))];
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
        var code = TryExtractOracleCode(ex.Message);
        var hint = BuildHint(code, ex.Message);

        return $"Error executing query | code={code ?? "unknown"} | hint={hint}";
    }

    protected override string BuildHint(string? code, string message)
    {
        if (string.Equals(code, "ORA-00942", StringComparison.OrdinalIgnoreCase))
        {
            return "Table or view does not exist. Check 'TableName' and schema ownership (schema prefix may be required, e.g., 'SCHEMA.TABLE'). For CTEs, use 'CteConditions'.";
        }
        if (string.Equals(code, "ORA-00904", StringComparison.OrdinalIgnoreCase))
        {
            return "Invalid identifier (column not found). Check column names in 'SelectColumns', 'WhereConditions', etc. Oracle identifiers are case-sensitive when quoted; use uppercase unquoted names. For complex expressions, use 'Arithmetic' or 'CaseWhen' instead of raw strings in 'Field'.";
        }
        if (string.Equals(code, "ORA-00923", StringComparison.OrdinalIgnoreCase))
        {
            return "FROM keyword not found where expected. Ensure 'TableName' is specified and the query structure is valid.";
        }
        if (string.Equals(code, "ORA-00933", StringComparison.OrdinalIgnoreCase))
        {
            return "SQL command not properly ended. Check 'CombineConditions' (UNION/INTERSECT) structure, or trailing ORDER BY/LIMIT usage.";
        }
        if (string.Equals(code, "ORA-00920", StringComparison.OrdinalIgnoreCase))
        {
            return "Invalid relational operator. Check 'Operator' values in 'WhereConditions' (e.g., '=', '>', '<', 'LIKE', 'IN').";
        }
        if (string.Equals(code, "ORA-01722", StringComparison.OrdinalIgnoreCase))
        {
            return "Invalid number. The 'Value' cannot be converted to a number. Ensure numeric fields receive numeric values (not strings).";
        }
        if (string.Equals(code, "ORA-01843", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(code, "ORA-01861", StringComparison.OrdinalIgnoreCase))
        {
            return "Invalid date format. Ensure date values are in valid Oracle date format (e.g., 'YYYY-MM-DD'). Set 'IsDate': true for date comparisons in 'WhereConditions'.";
        }
        if (string.Equals(code, "ORA-00001", StringComparison.OrdinalIgnoreCase))
        {
            return "Unique constraint violation. The insert/update would create a duplicate value. Check your 'Values' for uniqueness.";
        }
        if (string.Equals(code, "ORA-02291", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(code, "ORA-02292", StringComparison.OrdinalIgnoreCase))
        {
            return "Integrity constraint violation (foreign key). Ensure the referenced record exists before inserting, or that no child records exist before deleting.";
        }
        if (string.Equals(code, "ORA-01476", StringComparison.OrdinalIgnoreCase))
        {
            return "Division by zero error. Check 'Arithmetic' expressions for division where the divisor could be zero.";
        }
        if (string.Equals(code, "ORA-00918", StringComparison.OrdinalIgnoreCase))
        {
            return "Ambiguous column. Use 'TableAlias.ColumnName' in 'Field' properties and 'Join' conditions to qualify the column.";
        }
        return base.BuildHint(code, message);
    }

    private static string? TryExtractOracleCode(string message)
    {
        var errorMatch = Regex.Match(message, @"(ORA-\d+)");
        if (errorMatch.Success) return errorMatch.Groups[1].Value;
        return null;
    }
}
