using System.Data.Common;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Models;
using SqlKata.Compilers;

namespace SqlAgent.Service.Strategies;

public partial class MsSqlServerStrategy(IQueryValueParserService valueParser, IConfiguration configuration) : BaseSqlStrategy(valueParser, configuration)
{
    public override SqlAgentToolType DbType => SqlAgentToolType.MsSqlServer;

    public override string BuildConnectionString(BuildDbConnectionModelBase model)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = model.Host,
            UserID = model.Username,
            Password = model.Password,
            InitialCatalog = model.Database
        };
        if (!string.IsNullOrEmpty(model.Port)) builder.DataSource += $",{model.Port}";
        if (!string.IsNullOrEmpty(model.ExtraSettings))
        {
            try
            {
                var settings = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(model.ExtraSettings);
                if (settings != null)
                {
                    if (settings.TryGetValue("TrustServerCertificate", out var trust)
                        && bool.TryParse(trust.ToString(), out var trustValue))
                        builder.TrustServerCertificate = trustValue;
                    if (settings.TryGetValue("Encrypt", out var encrypt)
                        && bool.TryParse(encrypt.ToString(), out var encryptValue))
                        builder.Encrypt = encryptValue;
                }
            }
            catch { }
        }
        return builder.ConnectionString;
    }

    public override DbConnection CreateConnection(string? connectionString) => new SqlConnection(connectionString);
    protected override Compiler CreateCompiler() => new SqlServerCompiler { UseLegacyPagination = true };

    public override async Task<List<string>> GetSchemasAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT name FROM sys.schemas WHERE principal_id = 1 OR name = 'dbo';";
        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return [.. await connection.QueryAsync<string>(new CommandDefinition(sql, cancellationToken: cancellationToken))];
    }

    public override async Task<List<string>> GetTablesAsync(string connectionString, string schemaName, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = CreateConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            const string sql = @"
            SELECT TABLE_NAME
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = @schemaName
            AND TABLE_TYPE = 'BASE TABLE';";
            var tables = await connection.QueryAsync<string>(new CommandDefinition(sql, new { schemaName }, cancellationToken: cancellationToken));
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

            const string sql = @"
            SELECT
                c.COLUMN_NAME,
                c.DATA_TYPE,
                CASE WHEN ic.column_id IS NULL THEN CAST(0 AS bit) ELSE CAST(1 AS bit) END AS IS_PRIMARY_KEY,
                ic.key_ordinal AS PRIMARY_KEY_ORDINAL
            FROM INFORMATION_SCHEMA.COLUMNS c
            LEFT JOIN sys.schemas s ON s.name = c.TABLE_SCHEMA
            LEFT JOIN sys.tables t ON t.schema_id = s.schema_id AND t.name = c.TABLE_NAME
            LEFT JOIN sys.indexes i ON i.object_id = t.object_id AND i.is_primary_key = 1
            LEFT JOIN sys.columns sc ON sc.object_id = t.object_id AND sc.name = c.COLUMN_NAME
            LEFT JOIN sys.index_columns ic
              ON ic.object_id = t.object_id
             AND ic.index_id = i.index_id
             AND ic.column_id = sc.column_id
            WHERE c.TABLE_SCHEMA = @schemaName AND c.TABLE_NAME = @tableName
            ORDER BY c.ORDINAL_POSITION";
            var rows = await connection.QueryAsync(new CommandDefinition(sql, new { schemaName, tableName }, cancellationToken: cancellationToken));
            return [.. rows.Select(r => new ColumnInfo(
                (string)r.COLUMN_NAME,
                (string)r.DATA_TYPE,
                (bool)r.IS_PRIMARY_KEY,
                r.PRIMARY_KEY_ORDINAL is null ? null : Convert.ToInt32(r.PRIMARY_KEY_ORDINAL)))];
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
        var code = ex is SqlException sqlEx ? sqlEx.Number.ToString() : TryExtractSqlCode(ex.Message);
        return $"Error executing query | code={code ?? "unknown"} | message={ex.GetBaseException().Message}";
    }

    private static string? TryExtractSqlCode(string message)
    {
        var errorMatch = SqlCodeRegex().Match(message);
        return errorMatch.Success ? errorMatch.Groups["code"].Value : null;
    }

    [GeneratedRegex(@"Error Number:\s*(?<code>\d+)")]
    private static partial Regex SqlCodeRegex();
}
