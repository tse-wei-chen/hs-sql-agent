using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;
using SqlKata.Compilers;
using System.Data.Common;
using System.Text.Json;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Interfaces;
using Microsoft.Extensions.Configuration;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.Strategies;

public class SqliteStrategy(IQueryValueParserService valueParser, IConfiguration configuration) : BaseSqlStrategy(valueParser, configuration)
{
    public override SqlAgentToolType DbType => SqlAgentToolType.Sqlite;
    public override string BuildConnectionString(BuildDbConnectionModelBase model)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = model.Database,
            Password = model.Password
        };
        return builder.ConnectionString;
    }
    public override DbConnection CreateConnection(string? connectionString) => new SqliteConnection(connectionString);
    protected override Compiler CreateCompiler() => new SqliteCompiler();

    public override async Task<List<string>> GetSchemasAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        // SQLite does not support multiple schemas in the same way as other RDBMS.
        // Returning an empty list or a list with a single default schema.
        return ["sqlite does not support schemas, please use get_tables to see available tables."];
    }

    public override async Task<List<string>> GetTablesAsync(string connectionString, string schemaName, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = CreateConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            return [.. await connection.QueryAsync<string>(new CommandDefinition("SELECT name FROM sqlite_master WHERE type='table';", cancellationToken: cancellationToken))];
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
            var checkSql = "SELECT name FROM sqlite_master WHERE type='table' AND name = @tbl;";
            var verifiedTableName = await connection.QueryFirstOrDefaultAsync<string>(checkSql, new { tbl = tableName });

            if (string.IsNullOrEmpty(verifiedTableName))
            {
                return [];
            }
            var sql = $"SELECT name AS COLUMN_NAME, type AS DATA_TYPE FROM pragma_table_info('{verifiedTableName}') ORDER BY cid";

            var result = await connection.QueryAsync(new CommandDefinition(sql, cancellationToken: cancellationToken));
            return [.. result.Select(r => new ColumnInfo((string)r.COLUMN_NAME, (string)r.DATA_TYPE))];
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
        var code = ex is SqliteException sqliteEx ? $"SQLITE_{sqliteEx.SqliteErrorCode}" : null;
        var hint = BuildHint(code, ex.Message);

        return $"Error executing query | code={code ?? "unknown"} | hint={hint}";
    }

    protected override string BuildHint(string? code, string message)
    {
        if (message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
            return "Table not found. Check 'TableName' and ensure the SQLite database file is correct. For CTEs, use the 'CteConditions' list.";

        if (message.Contains("no such column", StringComparison.OrdinalIgnoreCase))
            return "Column not found. Verify column names in 'SelectColumns', 'WhereConditions', or 'OrderBy'. For complex logic, use 'Arithmetic' or 'CaseWhen' instead of raw SQL in 'Field'.";

        if (message.Contains("syntax error", StringComparison.OrdinalIgnoreCase)
            || message.Contains("near", StringComparison.OrdinalIgnoreCase))
            return "SQL syntax issue. Check 'Operator' values (e.g., '=', 'IN', 'LIKE'), verify 'CombineConditions' types, and ensure 'SubQuery' has a valid structure.";

        if (message.Contains("datatype mismatch", StringComparison.OrdinalIgnoreCase)
            || message.Contains("type mismatch", StringComparison.OrdinalIgnoreCase))
            return "Data type mismatch. Ensure 'Value' type matches the column type. For date comparisons, set 'IsDate': true in the condition.";

        if (message.Contains("constraint failed", StringComparison.OrdinalIgnoreCase))
            return "Constraint violation. The insert/update violates a table constraint (e.g., NOT NULL, UNIQUE, FOREIGN KEY).";

        if (message.Contains("unable to open database", StringComparison.OrdinalIgnoreCase))
            return "Cannot open the SQLite database. Verify the connection string DataSource points to a valid database file path.";

        return base.BuildHint(code, message);
    }
}
