using Dapper;
using MySql.Data.MySqlClient;
using SqlKata.Compilers;
using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Interfaces;

using Microsoft.Extensions.Configuration;

namespace SqlAgent.Service.Strategies;

public class MySqlStrategy(IQueryValueParserService valueParser, IConfiguration configuration) : BaseSqlStrategy(valueParser, configuration)
{
	public override SqlAgentToolType DbType => SqlAgentToolType.MySQL;

	protected override DbConnection CreateConnection(string? connectionString) => new MySqlConnection(connectionString);
	protected override Compiler CreateCompiler() => new MySqlCompiler();

	public override async Task<List<string>> GetSchemasAsync(string connectionString, CancellationToken cancellationToken = default)
	{
		try
		{
			using var connection = new MySqlConnection(connectionString);
			await connection.OpenAsync(cancellationToken);
			return [.. await connection.QueryAsync<string>("SHOW DATABASES;")];
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
			using var connection = new MySqlConnection(connectionString);
			await connection.OpenAsync(cancellationToken);
			var sql = @"
            SELECT TABLE_NAME 
            FROM information_schema.TABLES 
            WHERE TABLE_SCHEMA = @schemaName
            AND TABLE_TYPE = 'BASE TABLE';";

			var tables = await connection.QueryAsync<string>(sql, new { schemaName });
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

	public override async Task<List<string>> GetColumnsAsync(string connectionString, string tableName, CancellationToken cancellationToken = default)
	{
		try
		{
			using var connection = new MySqlConnection(connectionString);
			await connection.OpenAsync(cancellationToken);
			var processedTableName = tableName.Contains('.')
				? tableName.Split('.').Last()
				: tableName;
			const string sql = @"
            SELECT COLUMN_NAME 
            FROM INFORMATION_SCHEMA.COLUMNS 
            WHERE TABLE_NAME = @tableName";

			var columns = await connection.QueryAsync<string>(sql, new { tableName = processedTableName });

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

	public override async Task<string> GetTableReferenceAsync(string connectionString, string tableName, CancellationToken cancellationToken = default)
	{
		try
		{
			using var connection = new MySqlConnection(connectionString);
			await connection.OpenAsync(cancellationToken);
			var processedTableName = tableName.Contains('.')
				? tableName.Split('.').Last()
				: tableName;
			const string sql = """
				SELECT
					kcu.TABLE_NAME AS SourceTable,
					kcu.COLUMN_NAME AS ForeignKey,
					kcu.REFERENCED_TABLE_NAME AS ReferenceTable,
					kcu.REFERENCED_COLUMN_NAME AS PrimaryKey
				FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
				INNER JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
					ON tc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME
					AND tc.TABLE_SCHEMA = kcu.TABLE_SCHEMA
					AND tc.TABLE_NAME = kcu.TABLE_NAME
				WHERE kcu.TABLE_SCHEMA = DATABASE()
					AND kcu.TABLE_NAME = @tableName
					AND tc.CONSTRAINT_TYPE = 'FOREIGN KEY'
				ORDER BY kcu.REFERENCED_TABLE_NAME, kcu.COLUMN_NAME;
				""";

			var references = await connection.QueryAsync(sql, new { tableName = processedTableName });
			var result = references.Select(r => new
			{
				SourceTable = (string)r.SourceTable,
				ReferenceTable = (string)r.ReferenceTable,
				PrimaryKey = (string)r.PrimaryKey,
				ForeignKey = (string)r.ForeignKey
			}).ToList();

			return JsonSerializer.Serialize(result);
		}
		catch (Exception ex)
		{
			throw new Exception(@$"
				Error getting table reference: {ex.Message},
				please try again !!
			");
		}
	}

	protected override string BuildExecutionErrorMessage(Exception ex, string type)
	{
		var code = ex is MySqlException mysqlEx2 ? mysqlEx2.Number.ToString() : TryExtractMySqlCode(ex.Message);
		var hint = BuildHint(code, ex.Message);

		return $"Error executing query | code={code ?? "unknown"} | hint={hint}";
	}

	protected override string BuildHint(string? code, string message)
	{
		if (string.Equals(code, "1064", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(code, "42000", StringComparison.OrdinalIgnoreCase))
		{
			if (message.Contains("date", StringComparison.OrdinalIgnoreCase) || message.Contains("time", StringComparison.OrdinalIgnoreCase))
			{
				return "Date comparison syntax error. Fix: Use 'IsDate': true in WhereCondition, or ensure your 'Value' is a valid ISO date string.";
			}

			return "SQL syntax error. Tips: 1. Use 'Arithmetic' object for math instead of raw strings in 'Field'. 2. Check 'Operator' compatibility (e.g., '=', 'IN', 'LIKE'). 3. Ensure 'CombineConditions' type is correct.";
		}

		if (string.Equals(code, "42S02", StringComparison.OrdinalIgnoreCase)
			|| message.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase))
		{
			return "Table or CTE not found. Check 'TableName'. Note: If using 'FromQuery' (Subquery in FROM), ensure you provide an 'Alias'.";
		}

		if (string.Equals(code, "42S22", StringComparison.OrdinalIgnoreCase)
			|| message.Contains("Unknown column", StringComparison.OrdinalIgnoreCase))
		{
			return "Column not found. Tips: 1. For complex logic, use 'CaseWhen' or 'Arithmetic' instead of writing raw SQL in 'Field'. 2. In Joins/OrderBy, use 'TableName.FieldName' to avoid ambiguity.";
		}

		if (string.Equals(code, "1292", StringComparison.OrdinalIgnoreCase))
		{
			return "Truncated/Incorrect value. Check if your 'Value' matches the column data type (e.g., passing a string to an Integer field).";
		}

		if (message.Contains("Operand should contain 1 column", StringComparison.OrdinalIgnoreCase))
		{
			return "Subquery returns too many columns. When using 'SubQuery' in a WhereCondition, ensure the inner 'SelectColumns' list has only ONE column.";
		}

		return base.BuildHint(code, message);
	}

	private static string? TryExtractMySqlCode(string message)
	{
		if (string.IsNullOrWhiteSpace(message)) return null;

		var sqlState = Regex.Match(message, @"SQLSTATE\[(?<code>[0-9A-Z]{5})\]", RegexOptions.IgnoreCase);
		if (sqlState.Success) return sqlState.Groups["code"].Value.ToUpperInvariant();

		var mysqlCode = Regex.Match(message, @"\b(?<code>\d{4})\b");
		if (mysqlCode.Success) return mysqlCode.Groups["code"].Value;

		return null;
	}
}