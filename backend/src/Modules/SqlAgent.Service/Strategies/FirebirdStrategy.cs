using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Configuration;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.Strategies;

public partial class FirebirdStrategy(IQueryValueParserService valueParser, IConfiguration configuration) : BaseSqlStrategy(valueParser, configuration)
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

    public override async Task<List<string>> GetSchemasAsync(string connectionString, CancellationToken cancellationToken = default)
        => await Task.FromResult(new List<string> { "Default" });

    public override async Task<List<string>> GetTablesAsync(string connectionString, string schemaName, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = CreateConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            const string sql = "SELECT TRIM(RDB$RELATION_NAME) FROM RDB$RELATIONS WHERE RDB$SYSTEM_FLAG = 0 AND RDB$VIEW_BLR IS NULL;";
            return [.. await connection.QueryAsync<string>(sql)];
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
                   END AS DATA_TYPE,
                   CASE WHEN pk.RDB$FIELD_NAME IS NULL THEN 0 ELSE 1 END AS IS_PRIMARY_KEY,
                   CASE WHEN pk.RDB$FIELD_POSITION IS NULL THEN NULL ELSE pk.RDB$FIELD_POSITION + 1 END AS PRIMARY_KEY_ORDINAL
            FROM RDB$RELATION_FIELDS f
            JOIN RDB$FIELDS fs ON fs.RDB$FIELD_NAME = f.RDB$FIELD_SOURCE
            JOIN RDB$TYPES t ON t.RDB$TYPE = fs.RDB$FIELD_TYPE AND t.RDB$FIELD_NAME = 'RDB$FIELD_TYPE'
            LEFT JOIN (
                SELECT rc.RDB$RELATION_NAME, seg.RDB$FIELD_NAME, seg.RDB$FIELD_POSITION
                FROM RDB$RELATION_CONSTRAINTS rc
                JOIN RDB$INDEX_SEGMENTS seg ON seg.RDB$INDEX_NAME = rc.RDB$INDEX_NAME
                WHERE rc.RDB$CONSTRAINT_TYPE = 'PRIMARY KEY'
            ) pk
              ON pk.RDB$RELATION_NAME = f.RDB$RELATION_NAME
             AND pk.RDB$FIELD_NAME = f.RDB$FIELD_NAME
            WHERE f.RDB$RELATION_NAME = UPPER(@tableName)
            ORDER BY f.RDB$FIELD_POSITION;";

            var rows = await connection.QueryAsync(sql, new { tableName });
            return [.. rows.Select(r => new ColumnInfo(
                ((string)r.COLUMN_NAME).TrimEnd(),
                ((string)r.DATA_TYPE).TrimEnd(),
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
        var iscCode = ex is FbException fbEx ? fbEx.ErrorCode.ToString() : null;
        var code = TryExtractFbSqlCode(ex.Message) ?? iscCode;
        return $"Error executing query | code={code ?? "unknown"} | message={ex.GetBaseException().Message}";
    }

    private static string? TryExtractFbSqlCode(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var sqlCodeMatch = SqlCodeRegex().Match(message);
        if (sqlCodeMatch.Success) return "FB_SQL_" + sqlCodeMatch.Groups["code"].Value;
        var gdsCodeMatch = GdsCodeRegex().Match(message);
        if (gdsCodeMatch.Success) return "FB_GDS_" + gdsCodeMatch.Groups["code"].Value;
        return null;
    }

    [GeneratedRegex(@".*gds\s+code\s*=\s*(?<code>\d+)", RegexOptions.IgnoreCase, "zh-TW")]
    private static partial Regex GdsCodeRegex();
    [GeneratedRegex(@"SQL\s+(?:error\s+)?[Cc]ode\s*=\s*(?<code>-?\d+)", RegexOptions.IgnoreCase, "zh-TW")]
    private static partial Regex SqlCodeRegex();
}
