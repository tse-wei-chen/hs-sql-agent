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

public partial class PostgresStrategy(IQueryValueParserService valueParser, IConfiguration configuration) : BaseSqlStrategy(valueParser, configuration)
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

    public override async Task<List<ColumnInfo>> GetColumnsAsync(string connectionString, string schemaName, string tableName, CancellationToken cancellationToken = default)
    {
        const string sql = @"
            SELECT column_name, data_type
            FROM information_schema.columns 
            WHERE table_schema = @schemaName 
            AND table_name = @tableName
            ORDER BY ordinal_position;";

        try
        {
            using var connection = CreateConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            var rows = await connection.QueryAsync(new CommandDefinition(sql, new { schemaName, tableName }, cancellationToken: cancellationToken));

            return [.. rows.Select(r => new ColumnInfo((string)r.column_name, (string)r.data_type))];
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
        var pgEx = FindPostgresException(ex);
        var code = pgEx?.SqlState ?? TryExtractSqlStateCode(ex.ToString());
        var message = pgEx?.MessageText ?? ex.GetBaseException().Message;

        return $"Error executing query | code={code ?? "unknown"} | message={message}";
    }

    private static string? TryExtractSqlStateCode(string message)
    {
        var match = Regex.Match(message ?? string.Empty, @"\b(?<code>[0-9A-Z]{5})\b");
        if (!match.Success) return null;

        var code = match.Groups["code"].Value;
        return code.Any(char.IsDigit) ? code : null;
    }

    private static PostgresException? FindPostgresException(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is PostgresException pgEx)
                return pgEx;
        }

        return null;
    }
}
