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
        var hint = BuildHint(code, ex.Message);

        return $"Error executing query | code={code ?? "unknown"} | hint={hint}";
    }

    protected override string BuildHint(string? code, string message)
    {
        if (message.Contains("Table unknown", StringComparison.OrdinalIgnoreCase))
            return "Table does not exist. Check 'TableName'. Firebird table names are case-sensitive; ensure the table was created in the connected database.";

        if (message.Contains("Column unknown", StringComparison.OrdinalIgnoreCase))
            return "Column not found. Verify column names in 'SelectColumns' / 'WhereConditions'. Firebird column names are case-sensitive (use UPPERCASE for unquoted identifiers). For complex expressions, use 'Arithmetic' or 'CaseWhen' instead of raw SQL in 'Field'.";

        if (message.Contains("conversion error", StringComparison.OrdinalIgnoreCase)
            || message.Contains("expression evaluation", StringComparison.OrdinalIgnoreCase))
            return "Data type conversion error. Ensure 'Value' types match the column types. For date comparisons, use 'IsDate': true in the condition.";

        if (message.Contains("violation of", StringComparison.OrdinalIgnoreCase)
            && message.Contains("constraint", StringComparison.OrdinalIgnoreCase))
            return "Constraint violation. The operation violates a PRIMARY KEY, UNIQUE, or FOREIGN KEY constraint. Check your data values.";

        if (message.Contains("arithmetic exception", StringComparison.OrdinalIgnoreCase)
            || message.Contains("divide by zero", StringComparison.OrdinalIgnoreCase)
            || message.Contains("division by zero", StringComparison.OrdinalIgnoreCase))
            return "Division by zero or numeric overflow. Check 'Arithmetic' expressions for zero divisors or values exceeding numeric precision.";

        if (message.Contains("overflow", StringComparison.OrdinalIgnoreCase))
            return "Numeric overflow. A value exceeds the column's numeric range. Check numeric values in 'Values' or 'Arithmetic' expressions.";

        return base.BuildHint(code, message);
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
