using Dapper;
using MySql.Data.MySqlClient;
using SqlKata.Compilers;
using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Interfaces;

namespace SqlAgent.Service.Strategies;

public class MySqlStrategy : BaseSqlStrategy
{
	public MySqlStrategy(IValidator validator, IQueryValueParserService valueParser)
		: base(validator, valueParser)
	{
	}

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
			const string sql = @"
            SELECT COLUMN_NAME 
            FROM INFORMATION_SCHEMA.COLUMNS 
            WHERE TABLE_NAME = @tableName";

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

	public override async Task<string> GetTableReferenceAsync(string connectionString, string tableName, CancellationToken cancellationToken = default)
	{
		try
		{
			using var connection = new MySqlConnection(connectionString);
			await connection.OpenAsync(cancellationToken);

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

			var references = await connection.QueryAsync(sql, new { tableName });
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

	protected override string BuildExecutionErrorMessage(Exception ex)
	{
		var code = TryExtractMySqlCode(ex.Message);
		var hint = BuildHint(code, ex.Message);
		var action = BuildNextAction(code, ex.Message);

		return $"Error executing query | code={code ?? "unknown"} | hint={hint} | nextAction={action}";
	}

	protected override string BuildHint(string? code, string message)
	{
		if (string.Equals(code, "1064", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(code, "42000", StringComparison.OrdinalIgnoreCase))
		{
			return "SQL syntax/operator issue. Verify combine type, string match mode, and field/operator compatibility.";
		}

		if (string.Equals(code, "42S02", StringComparison.OrdinalIgnoreCase)
			|| message.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase))
		{
			return "Table not found. Use schema-qualified table names, for example northwind.customers.";
		}

		if (string.Equals(code, "42S22", StringComparison.OrdinalIgnoreCase)
			|| message.Contains("Unknown column", StringComparison.OrdinalIgnoreCase))
		{
			return "Column not found. Confirm field names and table prefixes in joins/group/order clauses.";
		}

		return base.BuildHint(code, message);
	}

	protected override string BuildNextAction(string? code, string message)
	{
		if (string.Equals(code, "1064", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(code, "42000", StringComparison.OrdinalIgnoreCase))
		{
			return "Retry with simplified query first (select + where), then add combine/string conditions step-by-step.";
		}

		if (string.Equals(code, "42S02", StringComparison.OrdinalIgnoreCase)
			|| message.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase))
		{
			return "Retry with fully-qualified table name and verify schema via get_tables in that schema.";
		}

		if (string.Equals(code, "42S22", StringComparison.OrdinalIgnoreCase)
			|| message.Contains("Unknown column", StringComparison.OrdinalIgnoreCase))
		{
			return "Retry with valid columns from get_columns and qualify fields in joins (table.column).";
		}

		return base.BuildNextAction(code, message);
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