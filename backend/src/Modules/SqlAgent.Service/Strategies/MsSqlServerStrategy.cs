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
            DataSource = $"{model.Host},{model.Port}",
            UserID = model.Username,
            Password = model.Password,
            InitialCatalog = model.Database
        };
        if (!string.IsNullOrEmpty(model.Port))
        {
            builder.DataSource += $",{model.Port}";
        }
        return builder.ConnectionString;
    }

    public override DbConnection CreateConnection(string? connectionString) => new SqlConnection(connectionString);
    protected override Compiler CreateCompiler() => new SqlServerCompiler();

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
            return "Invalid column name. Check 'SelectColumns' or 'WhereConditions'.";
        }
        if (string.Equals(code, "208", StringComparison.OrdinalIgnoreCase))
        {
            return "Invalid object name. Check 'TableName'.";
        }
        if (string.Equals(code, "156", StringComparison.OrdinalIgnoreCase) || string.Equals(code, "102", StringComparison.OrdinalIgnoreCase))
        {
            return "Incorrect syntax near keyword. Verify your conditions and arithmetic usage.";
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
