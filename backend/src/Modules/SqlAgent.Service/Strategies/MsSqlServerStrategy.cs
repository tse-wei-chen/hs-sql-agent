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

public class MsSqlServerStrategy(IQueryValueParserService valueParser, IConfiguration configuration) : BaseSqlStrategy(valueParser, configuration)
{
    public override SqlAgentToolType DbType => SqlAgentToolType.MsSqlServer;

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
        var hint = BuildHint(code, ex.Message);

        return $"Error executing query | code={code ?? "unknown"} | hint={hint}";
    }

    protected override string BuildHint(string? code, string message)
    {
        if (string.Equals(code, "207", StringComparison.OrdinalIgnoreCase))
        {
            return "Invalid column name. Check 'SelectColumns' or 'WhereConditions'. For derived/calculated columns, use 'Arithmetic' or 'CaseWhen' instead of raw SQL in 'Field'. Use 'TableAlias.ColumnName' in joins to avoid ambiguity.";
        }
        if (string.Equals(code, "208", StringComparison.OrdinalIgnoreCase))
        {
            return "Invalid object name (table/view not found). Check 'TableName' and schema prefix (e.g., 'dbo.TableName'). For CTEs, ensure 'CteConditions' is used with a valid subquery.";
        }
        if (string.Equals(code, "156", StringComparison.OrdinalIgnoreCase) || string.Equals(code, "102", StringComparison.OrdinalIgnoreCase))
        {
            return "Incorrect syntax near keyword. Verify your conditions and arithmetic usage. Check that 'SubQuery' has valid structure, 'CombineConditions' have matching columns, and 'Operator' values are valid.";
        }
        if (string.Equals(code, "515", StringComparison.OrdinalIgnoreCase))
        {
            return "Cannot insert NULL into a NOT NULL column. Ensure all required fields in 'Values' are provided and non-null.";
        }
        if (string.Equals(code, "2627", StringComparison.OrdinalIgnoreCase) || string.Equals(code, "2601", StringComparison.OrdinalIgnoreCase))
        {
            return "Unique constraint violation. The insert/update would create a duplicate value. Check your data for uniqueness conflicts.";
        }
        if (string.Equals(code, "547", StringComparison.OrdinalIgnoreCase))
        {
            return "Foreign key constraint violation. The referenced record does not exist. Insert the referenced record first or correct the foreign key value.";
        }
        if (string.Equals(code, "245", StringComparison.OrdinalIgnoreCase) || string.Equals(code, "8114", StringComparison.OrdinalIgnoreCase))
        {
            return "Data type conversion error. Ensure 'Value' types match the column types. For date comparisons, use 'IsDate': true in the condition.";
        }
        if (string.Equals(code, "1205", StringComparison.OrdinalIgnoreCase))
        {
            return "Deadlock occurred. The query was chosen as a deadlock victim. Retry the operation.";
        }
        if (string.Equals(code, "8134", StringComparison.OrdinalIgnoreCase))
        {
            return "Division by zero error. Check arithmetic expressions where divisor could be zero. Use NULLIF(denominator, 0) to guard against division by zero.";
        }
        return base.BuildHint(code, message);
    }

    private static string? TryExtractSqlCode(string message)
    {
        var errorMatch = Regex.Match(message, @"Error Number:\s*(?<code>\d+)");
        if (errorMatch.Success) return errorMatch.Groups["code"].Value;
        return null;
    }
}
