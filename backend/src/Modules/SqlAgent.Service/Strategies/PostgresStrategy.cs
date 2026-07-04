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

public class PostgresStrategy(IQueryValueParserService valueParser, IConfiguration configuration) : BaseSqlStrategy(valueParser, configuration)
{
    public override SqlAgentToolType DbType => SqlAgentToolType.Postgres;

    protected override IReadOnlyDictionary<string, string> FunctionNameMappings => new Dictionary<string, string>
    {
        ["DATE_FORMAT"] = "TO_CHAR",
        ["FORMAT"] = "TO_CHAR",
        ["IFNULL"] = "COALESCE",
        ["NVL"] = "COALESCE",
        ["ISNULL"] = "COALESCE",
        ["RAND"] = "RANDOM",
        ["LEN"] = "LENGTH",
        ["CEILING"] = "CEIL",
        ["REPLICATE"] = "REPEAT",
        ["LISTAGG"] = "STRING_AGG",
        ["LIST"] = "STRING_AGG",
    };

    protected override IReadOnlyDictionary<string, string> FunctionTemplates => new Dictionary<string, string>
    {
        ["NOW"] = "@CurrentTimestamp",
        ["SYSDATE"] = "@CurrentTimestamp",
        ["GETDATE"] = "@CurrentTimestamp",

        // DATEDIFF — date subtraction returns integer days in PG
        ["DATEDIFF($1, $2)"] = "$1 - $2",
        ["DATEDIFF($1, $2, $3)"] = "$2 - $3",

        // Date part extraction — use DATE_PART (comma-separated args, engine-safe)
        ["YEAR($1)"] = "DATE_PART('year', $1)",
        ["MONTH($1)"] = "DATE_PART('month', $1)",
        ["DAY($1)"] = "DATE_PART('day', $1)",

        // LOCATE/INSTR/CHARINDEX → STRPOS — all need arg reversal vs STRPOS(str,substr)
        ["LOCATE($1, $2)"] = "STRPOS($2, $1)",
        ["INSTR($1, $2)"] = "STRPOS($1, $2)",    // INSTR(str,substr) same order as STRPOS
        ["CHARINDEX($1, $2)"] = "STRPOS($2, $1)", // CHARINDEX(substr,str) reversed

        // Date formatting: always produce TO_CHAR with Postgres-native format,
        // regardless of which dialect the LLM used. The :date_format('...') arg
        // pins the output format directly — no runtime conversion needed.
        ["DATE_FORMAT($1, $2)"] = "TO_CHAR($1, $2:date_format('pg'))",
        ["FORMAT($1, $2)"] = "TO_CHAR($1, $2:date_format('pg'))",
        ["TO_CHAR($1, $2)"] = "TO_CHAR($1, $2:date_format('pg'))",
        ["STRFTIME($1, $2)"] = "TO_CHAR($2, $1:date_format('pg'))",

        // GROUP_CONCAT → STRING_AGG (separator required in PG)
        ["GROUP_CONCAT($1)"] = "STRING_AGG($1, ',')",
        ["GROUP_CONCAT($1, $2)"] = "STRING_AGG($1, $2)",
    };

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
