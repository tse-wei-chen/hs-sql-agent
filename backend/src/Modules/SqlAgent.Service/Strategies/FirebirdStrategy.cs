using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Configuration;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Models;
using SqlKata.Compilers;

namespace SqlAgent.Service.Strategies;

public partial class FirebirdStrategy(IQueryValueParserService valueParser, IConfiguration configuration) : BaseSqlStrategy(valueParser, configuration)
{
    public override SqlAgentToolType DbType => SqlAgentToolType.Firebird;

    protected override IReadOnlyDictionary<string, string> FunctionNameMappings => new Dictionary<string, string>
    {
        ["IFNULL"] = "COALESCE",
        ["NVL"] = "COALESCE",
        ["ISNULL"] = "COALESCE",
        ["LOCATE"] = "POSITION",
        ["LEN"] = "CHAR_LENGTH",
        ["CEILING"] = "CEIL",
        ["RANDOM"] = "RAND",
        ["STRING_AGG"] = "LIST",
        ["LISTAGG"] = "LIST",
    };

    protected override IReadOnlyDictionary<string, string> FunctionTemplates => new Dictionary<string, string>
    {
        ["GETDATE"] = "@CurrentTimestamp",
        ["SYSDATE"] = "@CurrentTimestamp",

        // 2-arg DATEDIFF → needs unit + arg reorder
        ["DATEDIFF($1, $2)"] = "DATEDIFF(@Day, $2, $1)",

        // STRPOS(str,substr) → POSITION(substr,str) reversed
        ["STRPOS($1, $2)"] = "POSITION($2, $1)",

        // INSTR(str,substr) → POSITION(substr,str) reversed
        ["INSTR($1, $2)"] = "POSITION($2, $1)",

        // CHARINDEX(substr,str) → POSITION(substr,str)
        ["CHARINDEX($1, $2)"] = "POSITION($1, $2)",

        // GROUP_CONCAT → LIST
        ["GROUP_CONCAT($1)"] = "LIST($1, ',')",
        ["GROUP_CONCAT($1, $2)"] = "LIST($1, $2)",
    };

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
        var iscCode = ex is FbException fbEx ? fbEx.ErrorCode.ToString() : null;
        var code = TryExtractFbSqlCode(ex.Message) ?? iscCode;

        return $"Error executing query | code={code ?? "unknown"} | message={ex.GetBaseException().Message}";
    }

    private static string? TryExtractFbSqlCode(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;

        // Extract SQL code pattern: "SQL error code = -204" or "SQL Code = -204"
        var sqlCodeMatch = SqlCodeRegex().Match(message);
        if (sqlCodeMatch.Success)
        {
            var rawCode = sqlCodeMatch.Groups["code"].Value;
            return "FB_SQL_" + rawCode;
        }

        // Extract gds code pattern: "gds code = 335544569"
        var gdsCodeMatch = GdsCodeRegex().Match(message);
        if (gdsCodeMatch.Success)
        {
            return "FB_GDS_" + gdsCodeMatch.Groups["code"].Value;
        }

        return null;
    }

    [GeneratedRegex(@".*gds\s+code\s*=\s*(?<code>\d+)", RegexOptions.IgnoreCase, "zh-TW")]
    private static partial Regex GdsCodeRegex();
    [GeneratedRegex(@"SQL\s+(?:error\s+)?[Cc]ode\s*=\s*(?<code>-?\d+)", RegexOptions.IgnoreCase, "zh-TW")]
    private static partial Regex SqlCodeRegex();
}
