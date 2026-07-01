using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.Configuration;
using MySql.Data.MySqlClient;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Models;
using SqlKata.Compilers;

namespace SqlAgent.Service.Strategies;

public partial class MySqlStrategy(IQueryValueParserService valueParser, IConfiguration configuration) : BaseSqlStrategy(valueParser, configuration)
{
    public override SqlAgentToolType DbType => SqlAgentToolType.MySQL;

    // Translate other dialects' function names INTO MySQL native functions
    protected override IReadOnlyDictionary<string, string> FunctionNameMappings => new Dictionary<string, string>
    {
        ["TO_CHAR"] = "DATE_FORMAT",
        ["FORMAT"] = "DATE_FORMAT",
        ["NVL"] = "IFNULL",
        ["ISNULL"] = "IFNULL",
        ["LEN"] = "CHAR_LENGTH",
        ["CEILING"] = "CEIL",
        ["REPLICATE"] = "REPEAT",
        ["RANDOM"] = "RAND",
        ["GETDATE"] = "NOW",
        ["SYSDATE"] = "NOW",
    };

    // MySQL native: DATEDIFF(date1,date2), DATE_FORMAT(date,fmt),
    // IFNULL(expr,default), NOW(), YEAR/MONTH/DAY, GROUP_CONCAT, RAND.
    // Templates handle structural differences from other dialects.
    protected override IReadOnlyDictionary<string, string> FunctionTemplates => new Dictionary<string, string>
    {
        // MSSQL/Firebird 3-arg DATEDIFF(unit,date1,date2) → TIMESTAMPDIFF (same arg order)
        ["DATEDIFF($1, $2, $3)"] = "TIMESTAMPDIFF($1, $2, $3)",

        // SQLite STRFTIME(fmt, date) → DATE_FORMAT(date, fmt) — arg order reversed
        ["STRFTIME($1, $2)"] = "DATE_FORMAT($2, $1)",

        // PG STRPOS(str, substr) → LOCATE(substr, str) — arg order reversed
        ["STRPOS($1, $2)"] = "LOCATE($2, $1)",

        // Oracle INSTR(str, substr) → LOCATE(substr, str) — arg order reversed
        ["INSTR($1, $2)"] = "LOCATE($2, $1)",
    };

    public override string BuildConnectionString(BuildDbConnectionModelBase model)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = model.Host,
            Port = string.IsNullOrEmpty(model.Port) ? 3306 : uint.Parse(model.Port),
            UserID = model.Username,
            Password = model.Password,
            Database = model.Database
        };
        return builder.ConnectionString;
    }
    public override DbConnection CreateConnection(string? connectionString) => new MySqlConnection(connectionString);
    protected override Compiler CreateCompiler() => new MySqlCompiler();

    public override async Task<List<string>> GetSchemasAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = CreateConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            return [.. await connection.QueryAsync<string>(new CommandDefinition("SHOW DATABASES;", cancellationToken: cancellationToken))];
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
            var sql = @"
            SELECT TABLE_NAME
            FROM information_schema.TABLES
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
                SELECT COLUMN_NAME, DATA_TYPE
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = @schemaName AND TABLE_NAME = @tableName
                ORDER BY ORDINAL_POSITION";

            var rows = await connection.QueryAsync(new CommandDefinition(sql, new { schemaName, tableName }, cancellationToken: cancellationToken));

            return [.. rows.Select(r => new ColumnInfo((string)r.COLUMN_NAME, (string)r.DATA_TYPE))];
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
        var code = ex is MySqlException mysqlEx2 ? mysqlEx2.Number.ToString() : TryExtractMySqlCode(ex.Message);

        return $"Error executing query | code={code ?? "unknown"} | message={ex.GetBaseException().Message}";
    }

    private static string? TryExtractMySqlCode(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;

        var sqlState = SqlStateRegex().Match(message);
        if (sqlState.Success) return sqlState.Groups["code"].Value.ToUpperInvariant();

        var mysqlCode = SqlCodeRegex().Match(message);
        if (mysqlCode.Success) return mysqlCode.Groups["code"].Value;

        return null;
    }

    [GeneratedRegex(@"SQLSTATE\[(?<code>[0-9A-Z]{5})\]", RegexOptions.IgnoreCase, "zh-TW")]
    private static partial Regex SqlStateRegex();
    [GeneratedRegex(@"\b(?<code>\d{4})\b")]
    private static partial Regex SqlCodeRegex();
}
