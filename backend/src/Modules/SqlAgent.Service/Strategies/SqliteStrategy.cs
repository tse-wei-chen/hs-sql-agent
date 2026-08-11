using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;
using SqlKata.Compilers;
using System.Data.Common;
using System.Text.Json;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Interfaces;
using Microsoft.Extensions.Configuration;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.Strategies;

public class SqliteStrategy(IQueryValueParserService valueParser, IConfiguration configuration) : BaseSqlStrategy(valueParser, configuration)
{
    public override SqlAgentToolType DbType => SqlAgentToolType.Sqlite;

    protected override IReadOnlyDictionary<string, string> FunctionNameMappings => new Dictionary<string, string>
    {
        ["IFNULL"] = "COALESCE",
        ["NVL"] = "COALESCE",
        ["ISNULL"] = "COALESCE",
        ["RAND"] = "RANDOM",
        ["LEN"] = "LENGTH",
        ["CEILING"] = "CEIL",
        ["STRING_AGG"] = "GROUP_CONCAT",
        ["LISTAGG"] = "GROUP_CONCAT",
        ["LIST"] = "GROUP_CONCAT",
        ["STRPOS"] = "INSTR",           // STRPOS(str,substr) → INSTR(str,substr) same order
    };

    protected override IReadOnlyDictionary<string, string> FunctionTemplates => new Dictionary<string, string>
    {
        ["GETDATE"] = "@CurrentTimestamp",
        ["SYSDATE"] = "@CurrentTimestamp",

        ["DATEDIFF($1, $2)"] = "JULIANDAY($1) - JULIANDAY($2)",

        // Date formatting: always produce STRFTIME with SQLite-native %-style format.
        // LLM may use MySQL/Oracle/MSSQL format strings; :date_format('%Y-%m-%d') pins the target.
        ["DATE_FORMAT($1, $2)"] = "STRFTIME($2:date_format('sqlite'), $1)",
        ["TO_CHAR($1, $2)"] = "STRFTIME($2:date_format('sqlite'), $1)",
        ["FORMAT($1, $2)"] = "STRFTIME($2:date_format('sqlite'), $1)",

        // Date part extraction
        ["YEAR($1)"] = "STRFTIME('%Y', $1)",
        ["MONTH($1)"] = "STRFTIME('%m', $1)",
        ["DAY($1)"] = "STRFTIME('%d', $1)",

        // CHARINDEX(substr,str) → INSTR(str,substr) reversed
        ["CHARINDEX($1, $2)"] = "INSTR($2, $1)",
    };

    public override string BuildConnectionString(BuildDbConnectionModelBase model)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = model.Database,
            Password = model.Password
        };
        return builder.ConnectionString;
    }
    public override DbConnection CreateConnection(string? connectionString) => new SqliteConnection(connectionString);
    protected override Compiler CreateCompiler() => new SqliteCompiler();

    public override async Task<List<string>> GetSchemasAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        // SQLite exposes the primary database as "main" and supports qualified
        // references such as main.orders. Returning a real identifier keeps
        // table-whitelist values executable and comparable with parsed SQL.
        return ["main"];
    }

    public override async Task<List<string>> GetTablesAsync(string connectionString, string schemaName, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = CreateConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            return [.. await connection.QueryAsync<string>(new CommandDefinition("SELECT name FROM sqlite_master WHERE type='table';", cancellationToken: cancellationToken))];
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
            var checkSql = "SELECT name FROM sqlite_master WHERE type='table' AND name = @tbl;";
            var verifiedTableName = await connection.QueryFirstOrDefaultAsync<string>(checkSql, new { tbl = tableName });

            if (string.IsNullOrEmpty(verifiedTableName))
            {
                return [];
            }
            var sql = $"SELECT name AS COLUMN_NAME, type AS DATA_TYPE FROM pragma_table_info('{verifiedTableName}') ORDER BY cid";

            var result = await connection.QueryAsync(new CommandDefinition(sql, cancellationToken: cancellationToken));
            return [.. result.Select(r => new ColumnInfo((string)r.COLUMN_NAME, (string)r.DATA_TYPE))];
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
        var code = ex is SqliteException sqliteEx ? $"SQLITE_{sqliteEx.SqliteErrorCode}" : null;

        return $"Error executing query | code={code ?? "unknown"} | message={ex.GetBaseException().Message}";
    }
}
