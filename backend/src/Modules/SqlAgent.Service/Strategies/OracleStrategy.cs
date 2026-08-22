using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.Configuration;
using Oracle.ManagedDataAccess.Client;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Models;
using SqlKata.Compilers;

namespace SqlAgent.Service.Strategies;

public partial class OracleStrategy(IQueryValueParserService valueParser, IConfiguration configuration) : BaseSqlStrategy(valueParser, configuration)
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
            const string sql = "SELECT TABLE_NAME FROM ALL_TABLES WHERE OWNER = :schemaName ORDER BY TABLE_NAME";
            var tables = await connection.QueryAsync<string>(sql, new { schemaName = schemaName.ToUpperInvariant() });
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
                    CASE WHEN pk.POSITION IS NULL THEN 0 ELSE 1 END AS IS_PRIMARY_KEY,
                    pk.POSITION AS PRIMARY_KEY_ORDINAL
                FROM ALL_TAB_COLUMNS c
                LEFT JOIN (
                    SELECT acc.OWNER, acc.TABLE_NAME, acc.COLUMN_NAME, acc.POSITION
                    FROM ALL_CONSTRAINTS ac
                    JOIN ALL_CONS_COLUMNS acc
                      ON acc.OWNER = ac.OWNER
                     AND acc.CONSTRAINT_NAME = ac.CONSTRAINT_NAME
                     AND acc.TABLE_NAME = ac.TABLE_NAME
                    WHERE ac.CONSTRAINT_TYPE = 'P'
                ) pk
                  ON pk.OWNER = c.OWNER
                 AND pk.TABLE_NAME = c.TABLE_NAME
                 AND pk.COLUMN_NAME = c.COLUMN_NAME
                WHERE c.OWNER = :schemaName AND c.TABLE_NAME = :tableName
                ORDER BY c.COLUMN_ID";
            var rows = await connection.QueryAsync(sql, new
            {
                schemaName = schemaName.ToUpperInvariant(),
                tableName = tableName.ToUpperInvariant()
            });
            return [.. rows.Select(r => new ColumnInfo(
                (string)r.COLUMN_NAME,
                (string)r.DATA_TYPE,
                Convert.ToInt32(r.IS_PRIMARY_KEY) != 0,
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
        var code = TryExtractOracleCode(ex.Message);
        return $"Error executing query | code={code ?? "unknown"} | message={ex.GetBaseException().Message}";
    }

    private static string? TryExtractOracleCode(string message)
    {
        var errorMatch = SqlCodeRegex().Match(message);
        return errorMatch.Success ? errorMatch.Groups[1].Value : null;
    }

    [GeneratedRegex(@"(ORA-\d+)")]
    private static partial Regex SqlCodeRegex();
}
