using Dapper;
using Npgsql;
using SqlKata.Compilers;
using System.Data.Common;
using System.Text.Json;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.Strategies;

public class PostgresStrategy : BaseSqlStrategy
{
	public PostgresStrategy(IQueryValueParserService valueParser)
		: base(valueParser)
	{
	}

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

	public override async Task<List<string>> GetTablesAsync(string connectionString, string schemaName, CancellationToken cancellationToken = default)
	{
		try
		{
			using var connection = new NpgsqlConnection(connectionString);
			await connection.OpenAsync(cancellationToken);
			return [.. await connection.QueryAsync<string>("SELECT table_name FROM information_schema.tables WHERE table_schema = @schemaName;", new { schemaName })];
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
			throw new Exception($"Error getting table reference: {ex.Message}", ex);
		}
	}

	protected override string BuildHint(string? code, string message)
	{
		if (string.Equals(code, "42883", StringComparison.OrdinalIgnoreCase))
		{
			if (message.Contains("date >= text", StringComparison.OrdinalIgnoreCase)
				|| message.Contains("date <= text", StringComparison.OrdinalIgnoreCase)
				|| message.Contains("date < text", StringComparison.OrdinalIgnoreCase)
				|| message.Contains("date > text", StringComparison.OrdinalIgnoreCase))
			{
				return "Date vs text type mismatch. Retry with dateWhereConditions.";
			}

			return "Operator/type mismatch. Retry with a compatible operator and typed values. Migration tip: use inWhereConditions for IN/NOT IN cases.";
		}

		if (string.Equals(code, "42703", StringComparison.OrdinalIgnoreCase))
			return "Column/expression not recognized. If using SQL expressions (e.g. date_part(...)), pass the full expression and avoid quoting it as a plain column name.";

		if (string.Equals(code, "42P01", StringComparison.OrdinalIgnoreCase))
			return "Relation not found. Check table/schema name, and for CTE ensure the CTE name is unqualified (use expensive_products, not public.expensive_products).";

		if (string.Equals(code, "42702", StringComparison.OrdinalIgnoreCase))
			return "Ambiguous column reference. Qualify fields with table prefixes, for example order_details.unit_price instead of unit_price.";

		if (string.Equals(code, "22P02", StringComparison.OrdinalIgnoreCase))
			return "Invalid value format for column type. Retry with correct literal format.";

		return base.BuildHint(code, message);
	}

	protected override string BuildNextAction(string? code, string message)
	{
		if (string.Equals(code, "42883", StringComparison.OrdinalIgnoreCase)
			&& (message.Contains("date >= text", StringComparison.OrdinalIgnoreCase)
				|| message.Contains("date <= text", StringComparison.OrdinalIgnoreCase)
				|| message.Contains("date < text", StringComparison.OrdinalIgnoreCase)
				|| message.Contains("date > text", StringComparison.OrdinalIgnoreCase)))
		{
			return "Retry and add dateWhereConditions. Example: { field: 'order_date', operator: '>=', value: '1997-01-01' }.";
		}

		if (string.Equals(code, "42702", StringComparison.OrdinalIgnoreCase))
			return "Retry by qualifying ambiguous columns with table names, for example 'order_details.unit_price'.";

		if (string.Equals(code, "42703", StringComparison.OrdinalIgnoreCase))
			return "Retry with an existing column or pass expression as raw field text, for example date_part('year', order_date).";

		if (string.Equals(code, "42P01", StringComparison.OrdinalIgnoreCase))
			return "Retry with an existing table/relation name. If this is a CTE query, reference the CTE by unqualified name only.";

		if (string.Equals(code, "22P02", StringComparison.OrdinalIgnoreCase))
			return "Retry with corrected literal format, for example number without quotes, ISO date string, or boolean true/false.";

		return base.BuildNextAction(code, message);
	}
}