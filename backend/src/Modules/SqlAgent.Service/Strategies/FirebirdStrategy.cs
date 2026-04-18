using Dapper;
using FirebirdSql.Data.FirebirdClient;
using SqlKata.Compilers;
using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Interfaces;

using Microsoft.Extensions.Configuration;

namespace SqlAgent.Service.Strategies;

public class FirebirdStrategy(IQueryValueParserService valueParser, IConfiguration configuration) : BaseSqlStrategy(valueParser, configuration)
{
    public override SqlAgentToolType DbType => SqlAgentToolType.Firebird;

    protected override DbConnection CreateConnection(string? connectionString) => new FbConnection(connectionString);
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

    public override async Task<List<string>> GetColumnsAsync(string connectionString, string schemaName, string tableName, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = CreateConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            const string sql = @"
            SELECT TRIM(RDB$FIELD_NAME) 
            FROM RDB$RELATION_FIELDS 
            WHERE RDB$RELATION_NAME = UPPER(@tableName)
            ORDER BY RDB$FIELD_POSITION;";

            var columns = await connection.QueryAsync<string>(sql, new { tableName });
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
        var code = ex is FbException fbEx ? fbEx.ErrorCode.ToString() : TryExtractFbSqlCode(ex.Message);
        var hint = BuildHint(code, ex.Message);

        return $"Error executing query | code={code ?? "unknown"} | hint={hint}";
    }

    protected override string BuildHint(string? code, string message)
    {
        if (message.Contains("Column unknown", StringComparison.OrdinalIgnoreCase))
        {
            return "Invalid column name. Check your Select, Where, or Join conditions.";
        }
        if (message.Contains("Table unknown", StringComparison.OrdinalIgnoreCase))
        {
            return "Table does not exist. Verify TableName.";
        }
        return base.BuildHint(code, message);
    }

    private static string? TryExtractFbSqlCode(string message)
    {
        return null;
    }
}
