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

        return $"Error executing query | code={code ?? "unknown"} | message={ex.GetBaseException().Message}";
    }
}
