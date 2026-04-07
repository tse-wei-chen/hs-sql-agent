using Dapper;
using Npgsql;
using SqlKata.Compilers;
using System.Data.Common;
using System.Text.Json;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.Strategies;

public class PostgresStrategy : BaseSqlStrategy
{
	public override SqlAgentToolType DbType => SqlAgentToolType.Postgres;

	protected override DbConnection CreateConnection(string? connectionString) => new NpgsqlConnection(connectionString);
	protected override Compiler CreateCompiler() => new PostgresCompiler();

	public override async Task<List<string>> GetSchemasAsync(string connectionString, CancellationToken cancellationToken = default)
	{
		try
		{
			using var connection = new NpgsqlConnection(connectionString);
			await connection.OpenAsync(cancellationToken);
			return [.. await connection.QueryAsync<string>("SELECT schema_name FROM information_schema.schemata;")];
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
			using var connection = new NpgsqlConnection(connectionString);
			await connection.OpenAsync(cancellationToken);
			return [.. await connection.QueryAsync<string>("SELECT table_name FROM information_schema.tables WHERE table_schema = 'public';")];
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
			using var connection = new NpgsqlConnection(connectionString);
			await connection.OpenAsync(cancellationToken);
			return [.. await connection.QueryAsync<string>("SELECT column_name FROM information_schema.columns WHERE table_name = @tableName;", new { tableName })];
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
			using var connection = new NpgsqlConnection(connectionString);
			await connection.OpenAsync(cancellationToken);

			// 使用更精確的 JOIN 邏輯，並統一欄位命名為小寫
			const string sql = """
            SELECT
                kcu.table_name AS sourcetable,
                kcu.column_name AS foreignkey,
                rel_kcu.table_name AS referencetable,
                rel_kcu.column_name AS primarykey
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
                ON tc.constraint_name = kcu.constraint_name
                AND tc.table_schema = kcu.table_schema
            JOIN information_schema.referential_constraints rc
                ON tc.constraint_name = rc.constraint_name
            JOIN information_schema.key_column_usage rel_kcu
                ON rc.unique_constraint_name = rel_kcu.constraint_name
                AND rc.unique_constraint_schema = rel_kcu.table_schema
                AND kcu.ordinal_position = rel_kcu.ordinal_position
            WHERE tc.constraint_type = 'FOREIGN KEY'
                AND tc.table_name = @tableName
                AND tc.table_schema = 'public'
            ORDER BY rel_kcu.table_name, kcu.column_name;
            """;

			var references = await connection.QueryAsync<TableRefModel>(sql, new { tableName });

			return JsonSerializer.Serialize(references);
			
		}
		catch (Exception ex)
		{
			// 修正拋出異常的方式，保留原始 stack trace 建議使用 innerException
			throw new Exception($"Error getting table reference: {ex.Message}", ex);
		}
	}
}