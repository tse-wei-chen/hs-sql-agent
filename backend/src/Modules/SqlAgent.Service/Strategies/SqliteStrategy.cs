using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Interfaces;
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

    public override async Task<List<string>> GetSchemasAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        return ["main"];
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
            const string checkSql = "SELECT name FROM sqlite_master WHERE type='table' AND name = @tbl;";
            var verifiedTableName = await connection.QueryFirstOrDefaultAsync<string>(checkSql, new { tbl = tableName });

            if (string.IsNullOrEmpty(verifiedTableName)) return [];

            var sql = $"SELECT name AS COLUMN_NAME, type AS DATA_TYPE, pk AS PRIMARY_KEY_ORDINAL FROM pragma_table_info('{verifiedTableName.Replace("'", "''", StringComparison.Ordinal)}') ORDER BY cid";
            var result = await connection.QueryAsync(new CommandDefinition(sql, cancellationToken: cancellationToken));
            return [.. result.Select(r =>
            {
                var pkOrdinal = Convert.ToInt32(r.PRIMARY_KEY_ORDINAL);
                return new ColumnInfo(
                    (string)r.COLUMN_NAME,
                    (string)r.DATA_TYPE,
                    pkOrdinal > 0,
                    pkOrdinal > 0 ? pkOrdinal : null);
            })];
        }
        catch (Exception ex)
        {
            throw new Exception(@$"
                Error getting columns: {ex.Message},
                please try again !!
            ");
        }
    }
}
