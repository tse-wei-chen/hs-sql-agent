using Dapper;
using FirebirdSql.Data.FirebirdClient;
using SqlKata.Compilers;
using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Interfaces;
using Microsoft.Extensions.Configuration;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.Strategies;

public class FirebirdStrategy(IQueryValueParserService valueParser, IConfiguration configuration) : BaseSqlStrategy(valueParser, configuration)
{
    public override SqlAgentToolType DbType => SqlAgentToolType.Firebird;

    public override string BuildConnectionString(BuildDbConnectionModelBase model)
    {
        var builder = new FbConnectionStringBuilder
        {
            DataSource = model.Host,
            Port = string.IsNullOrEmpty(model.Port) ? 3050 : int.Parse(model.Port),
            UserID = model.Username,
            Password = model.Password,
            Database = model.Database
        };
        return builder.ConnectionString;
    }

    public override DbConnection CreateConnection(string? connectionString) => new FbConnection(connectionString);
    protected override Compiler CreateCompiler() => new FirebirdCompiler();

    public override async Task<List<string>> GetSchemasAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        // Firebird does not have identical concept of schemas inside a database file for structure
        return await Task.FromResult(new List<string> { "Default" });
    }

    public override async Task<List<string>> GetTablesAsync(string connectionString, string schemaName, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = CreateConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            var sql = "SELECT TRIM(RDB$RELATION_NAME) FROM RDB$RELATIONS WHERE RDB$SYSTEM_FLAG = 0 AND RDB$VIEW_BLR IS NULL;";
            var tables = await connection.QueryAsync<string>(sql);
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
            SELECT TRIM(f.RDB$FIELD_NAME) AS COLUMN_NAME,
                   CASE t.RDB$TYPE_NAME
                       WHEN 'SHORT'      THEN 'SMALLINT'
                       WHEN 'LONG'       THEN 'INTEGER'
                       WHEN 'INT64'      THEN 'BIGINT'
                       WHEN 'FLOAT'      THEN 'FLOAT'
                       WHEN 'DOUBLE'     THEN 'DOUBLE PRECISION'
                       WHEN 'VARYING'    THEN 'VARCHAR'
                       WHEN 'TEXT'       THEN 'CHAR'
                       WHEN 'BLOB'       THEN 'BLOB'
                       WHEN 'TIMESTAMP'  THEN 'TIMESTAMP'
                       WHEN 'SQL_DATE'   THEN 'DATE'
                       WHEN 'SQL_TIME'   THEN 'TIME'
                       WHEN 'BOOLEAN'    THEN 'BOOLEAN'
                       ELSE TRIM(t.RDB$TYPE_NAME)
                   END AS DATA_TYPE
            FROM RDB$RELATION_FIELDS f
            JOIN RDB$FIELDS fs ON fs.RDB$FIELD_NAME = f.RDB$FIELD_SOURCE
            JOIN RDB$TYPES t   ON t.RDB$TYPE = fs.RDB$FIELD_TYPE AND t.RDB$FIELD_NAME = 'RDB$FIELD_TYPE'
            WHERE f.RDB$RELATION_NAME = UPPER(@tableName)
            ORDER BY f.RDB$FIELD_POSITION;";

            var rows = await connection.QueryAsync(sql, new { tableName });
            return [.. rows.Select(r => new ColumnInfo(((string)r.COLUMN_NAME).TrimEnd(), ((string)r.DATA_TYPE).TrimEnd()))];
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
        var code = ex is FbException fbEx ? fbEx.ErrorCode.ToString() : TryExtractFbSqlCode(ex.Message);
        var hint = BuildHint(code, ex.Message);

        return $"Error executing query | code={code ?? "unknown"} | hint={hint}";
    }

    protected override string BuildHint(string? code, string message)
    {
        if (message.Contains("Column unknown", StringComparison.OrdinalIgnoreCase))
        {
            return "Invalid column name. Check your Select, Where, or Join conditions.";
        }
        if (message.Contains("Table unknown", StringComparison.OrdinalIgnoreCase))
        {
            return "Table does not exist. Verify TableName.";
        }
        return base.BuildHint(code, message);
    }

    private static string? TryExtractFbSqlCode(string message)
    {
        return null;
    }
}
