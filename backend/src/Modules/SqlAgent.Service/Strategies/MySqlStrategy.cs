using Dapper;
using MySql.Data.MySqlClient;
using SqlKata.Compilers;
using System.Data.Common;
using System.Text.Json;
using SqlAgent.Service.Enums;

namespace SqlAgent.Service.Strategies;

public class MySqlStrategy : BaseSqlStrategy
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

	public override async Task<List<string>> GetTablesAsync(string connectionString, CancellationToken cancellationToken = default)
	{
		try
		{
			using var connection = new MySqlConnection(connectionString);
			await connection.OpenAsync(cancellationToken);
			return [.. await connection.QueryAsync<string>("SHOW TABLES;")];
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
			return (await connection.QueryAsync<string>("SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @tableName", new { tableName })).ToList();
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
}