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

    protected override IReadOnlyDictionary<string, string> FunctionNameMappings => new Dictionary<string, string>
    {
        ["DATE_FORMAT"] = "TO_CHAR",
        ["FORMAT"] = "TO_CHAR",
        ["IFNULL"] = "NVL",
        ["ISNULL"] = "NVL",
        ["NOW"] = "CURRENT_TIMESTAMP",
        ["GETDATE"] = "CURRENT_TIMESTAMP",
        ["CEILING"] = "CEIL",
        ["LEN"] = "LENGTH",
        ["STRING_AGG"] = "LISTAGG",
        ["LIST"] = "LISTAGG",
        ["RANDOM"] = "RAND",             // Oracle RAND() exists, returns 0-1
    };

    protected override IReadOnlyDictionary<string, string> FunctionTemplates => new Dictionary<string, string>
    {
        ["NOW"] = "@CurrentTimestamp",
        ["GETDATE"] = "@CurrentTimestamp",
        ["SYSDATE"] = "@Sysdate",

        // Oracle date subtraction returns days
        ["DATEDIFF($1, $2)"] = "$1 - $2",
        ["DATEDIFF($1, $2, $3)"] = "$2 - $3",

        // LOCATE(substr, str) → INSTR(str, substr) — arg order reversed
        ["LOCATE($1, $2)"] = "INSTR($2, $1)",

        // STRPOS(str, substr) → INSTR(str, substr) — same order
        ["STRPOS($1, $2)"] = "INSTR($1, $2)",

        // CHARINDEX(substr, str) → INSTR(str, substr) — reversed
        ["CHARINDEX($1, $2)"] = "INSTR($2, $1)",

        // Date part extraction — use TO_CHAR (comma-separated args, engine-safe)
        ["YEAR($1)"] = "TO_CHAR($1, 'YYYY')",
        ["MONTH($1)"] = "TO_CHAR($1, 'MM')",
        ["DAY($1)"] = "TO_CHAR($1, 'DD')",

        // Date formatting: always produce TO_CHAR with Oracle-native format.
        ["DATE_FORMAT($1, $2)"] = "TO_CHAR($1, $2:date_format('YYYY-MM-DD'))",
        ["FORMAT($1, $2)"] = "TO_CHAR($1, $2:date_format('YYYY-MM-DD'))",
        ["TO_CHAR($1, $2)"] = "TO_CHAR($1, $2:date_format('YYYY-MM-DD'))",
        ["STRFTIME($1, $2)"] = "TO_CHAR($2, $1:date_format('YYYY-MM-DD'))",

        // GROUP_CONCAT → LISTAGG
        ["GROUP_CONCAT($1)"] = "LISTAGG($1, ',')",
        ["GROUP_CONCAT($1, $2)"] = "LISTAGG($1, $2)",
    };
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
            var sql = "SELECT TABLE_NAME FROM ALL_TABLES WHERE OWNER = :schemaName ORDER BY TABLE_NAME";
            var tables = await connection.QueryAsync<string>(sql, new { schemaName = schemaName.ToUpper() });
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

            var sql = "SELECT COLUMN_NAME, DATA_TYPE FROM ALL_TAB_COLUMNS WHERE OWNER = :schemaName AND TABLE_NAME = :tableName ORDER BY COLUMN_ID";
            var rows = await connection.QueryAsync(sql, new
            {
                schemaName = schemaName.ToUpper(),
                tableName = tableName.ToUpper()
            });
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
        var code = TryExtractOracleCode(ex.Message);

        return $"Error executing query | code={code ?? "unknown"} | message={ex.GetBaseException().Message}";
    }

    private static string? TryExtractOracleCode(string message)
    {
        var errorMatch = SqlCodeRegex().Match(message);
        if (errorMatch.Success) return errorMatch.Groups[1].Value;
        return null;
    }

    [GeneratedRegex(@"(ORA-\d+)")]
    private static partial Regex SqlCodeRegex();
}
