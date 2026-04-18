using Dapper;
using Oracle.ManagedDataAccess.Client;
using SqlKata.Compilers;
using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Interfaces;

using Microsoft.Extensions.Configuration;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.Strategies;

public class OracleStrategy(IQueryValueParserService valueParser, IConfiguration configuration) : BaseSqlStrategy(valueParser, configuration)
{
    public override SqlAgentToolType DbType => SqlAgentToolType.Oracle;
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

    public override async Task<List<string>> GetColumnsAsync(string connectionString, string schemaName, string tableName, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = CreateConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            var sql = "SELECT COLUMN_NAME FROM ALL_TAB_COLUMNS WHERE OWNER = :schemaName AND TABLE_NAME = :tableName ORDER BY COLUMN_ID";
            var columns = await connection.QueryAsync<string>(sql, new
            {
                schemaName = schemaName.ToUpper(),
                tableName = tableName.ToUpper()
            });
            return [.. columns];
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
        var hint = BuildHint(code, ex.Message);

        return $"Error executing query | code={code ?? "unknown"} | hint={hint}";
    }

    protected override string BuildHint(string? code, string message)
    {
        if (string.Equals(code, "ORA-00942", StringComparison.OrdinalIgnoreCase))
        {
            return "Table or view does not exist. Check 'TableName' and schema ownership.";
        }
        if (string.Equals(code, "ORA-00904", StringComparison.OrdinalIgnoreCase))
        {
            return "Invalid identifier. This usually means a column name is mistyped or doesn't exist.";
        }
        return base.BuildHint(code, message);
    }

    private static string? TryExtractOracleCode(string message)
    {
        var errorMatch = Regex.Match(message, @"(ORA-\d+)");
        if (errorMatch.Success) return errorMatch.Groups[1].Value;
        return null;
    }
}
