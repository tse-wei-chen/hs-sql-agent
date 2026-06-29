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
