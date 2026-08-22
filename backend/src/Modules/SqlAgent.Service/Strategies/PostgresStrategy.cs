using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Models;
using System.Data.Common;

namespace SqlAgent.Service.Strategies;

public class PostgresStrategy(IQueryValueParserService valueParser, IConfiguration configuration) : BaseSqlStrategy(valueParser, configuration)
{
    public override SqlAgentToolType DbType => SqlAgentToolType.Postgres;

    public override string BuildConnectionString(BuildDbConnectionModelBase model)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = model.Host,
            Port = string.IsNullOrEmpty(model.Port) ? 5432 : int.Parse(model.Port),
            Username = model.Username,
            Password = model.Password,
            Database = model.Database
        };
        return builder.ConnectionString;
    }

    public override DbConnection CreateConnection(string? connectionString) => new NpgsqlConnection(connectionString);

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

    public override async Task<List<ColumnInfo>> GetColumnsAsync(string connectionString, string schemaName, string tableName, CancellationToken cancellationToken = default)
    {
        const string sql = @"
            SELECT
                c.column_name,
                c.data_type,
                (pk.ordinal_position IS NOT NULL) AS is_primary_key,
                pk.ordinal_position AS primary_key_ordinal
            FROM information_schema.columns c
            LEFT JOIN (
                SELECT
                    kcu.column_name,
                    kcu.ordinal_position
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage kcu
                  ON tc.constraint_name = kcu.constraint_name
                 AND tc.constraint_schema = kcu.constraint_schema
                 AND tc.table_schema = kcu.table_schema
                 AND tc.table_name = kcu.table_name
                WHERE tc.constraint_type = 'PRIMARY KEY'
                  AND tc.table_schema = @schemaName
                  AND tc.table_name = @tableName
            ) pk ON pk.column_name = c.column_name
            WHERE c.table_schema = @schemaName
              AND c.table_name = @tableName
            ORDER BY c.ordinal_position;";

        try
        {
            using var connection = CreateConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            var rows = await connection.QueryAsync(new CommandDefinition(sql, new { schemaName, tableName }, cancellationToken: cancellationToken));

            return [.. rows.Select(r => new ColumnInfo(
                (string)r.column_name,
                (string)r.data_type,
                (bool)r.is_primary_key,
                r.primary_key_ordinal is null ? null : Convert.ToInt32(r.primary_key_ordinal)))];
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
