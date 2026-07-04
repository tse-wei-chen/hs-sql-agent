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

    protected override IReadOnlyDictionary<string, string> FunctionNameMappings => new Dictionary<string, string>
    {
        ["DATE_FORMAT"] = "FORMAT",
        ["TO_CHAR"] = "FORMAT",
        ["IFNULL"] = "ISNULL",
        ["NVL"] = "ISNULL",
        ["LENGTH"] = "LEN",
        ["LOCATE"] = "CHARINDEX",
        ["CEIL"] = "CEILING",
        ["REPEAT"] = "REPLICATE",
        ["RANDOM"] = "RAND",
        ["SYSDATE"] = "GETDATE",
        ["LISTAGG"] = "STRING_AGG",
        ["LIST"] = "STRING_AGG",
    };

    protected override IReadOnlyDictionary<string, string> FunctionTemplates => new Dictionary<string, string>
    {
        // 2-arg DATEDIFF(date1,date2) → needs DAY unit + arg reorder for MSSQL
        ["DATEDIFF($1, $2)"] = "DATEDIFF(@Day, $2, $1)",

        // EXTRACT(year FROM date) → DATEPART(year, date)
        // Note: YEAR/MONTH/DAY shorthands are native in MSSQL — no translation needed

        // Date formatting: always produce FORMAT with MSSQL-native .NET format.
        ["DATE_FORMAT($1, $2)"] = "FORMAT($1, $2:date_format('mssql'))",
        ["FORMAT($1, $2)"] = "FORMAT($1, $2:date_format('mssql'))",
        ["TO_CHAR($1, $2)"] = "FORMAT($1, $2:date_format('mssql'))",
        ["STRFTIME($1, $2)"] = "FORMAT($2, $1:date_format('mssql'))",

        // GROUP_CONCAT → STRING_AGG (separator required in MSSQL)
        ["GROUP_CONCAT($1)"] = "STRING_AGG($1, ',')",
        ["GROUP_CONCAT($1, $2)"] = "STRING_AGG($1, $2)",

        // STRPOS(str,substr) → CHARINDEX(substr,str) reversed
        ["STRPOS($1, $2)"] = "CHARINDEX($2, $1)",

        // INSTR(str,substr) → CHARINDEX(substr,str) reversed
        ["INSTR($1, $2)"] = "CHARINDEX($2, $1)",
    };

    public override string BuildConnectionString(BuildDbConnectionModelBase model)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = model.Host,
            UserID = model.Username,
            Password = model.Password,
            InitialCatalog = model.Database
        };
        if (!string.IsNullOrEmpty(model.Port))
        {
            builder.DataSource += $",{model.Port}";
        }

        if (!string.IsNullOrEmpty(model.ExtraSettings))
        {
            try
            {
                var settings = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(model.ExtraSettings);
                if (settings != null)
                {
                    if (settings.TryGetValue("TrustServerCertificate", out var trust))
                    {
                        if (bool.TryParse(trust.ToString(), out bool trustValue))
                        {
                            builder.TrustServerCertificate = trustValue;
                        }
                    }

                    if (settings.TryGetValue("Encrypt", out var encrypt))
                    {
                        if (bool.TryParse(encrypt.ToString(), out bool encryptValue))
                        {
                            builder.Encrypt = encryptValue;
                        }
                    }
                }
            }
            catch
            {
                // Ignore invalid JSON
            }
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
            var sql = @"
            SELECT TABLE_NAME
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = @schemaName
            AND TABLE_TYPE = 'BASE TABLE';";
            var command = new CommandDefinition(sql, new { schemaName }, cancellationToken: cancellationToken);
            var tables = await connection.QueryAsync<string>(command);
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
            var command = new CommandDefinition(sql, new { schemaName, tableName }, cancellationToken: cancellationToken);
            var rows = await connection.QueryAsync(command);
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
        var code = ex is SqlException sqlEx ? sqlEx.Number.ToString() : TryExtractSqlCode(ex.Message);

        return $"Error executing query | code={code ?? "unknown"} | message={ex.GetBaseException().Message}";
    }

    private static string? TryExtractSqlCode(string message)
    {
        var errorMatch = SqlCodeRegex().Match(message);
        if (errorMatch.Success) return errorMatch.Groups["code"].Value;
        return null;
    }

    [GeneratedRegex(@"Error Number:\s*(?<code>\d+)")]
    private static partial Regex SqlCodeRegex();
}
