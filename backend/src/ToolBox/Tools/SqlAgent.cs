
using System.ComponentModel;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Server;
using Admin.Service.Models;
using ToolBox.Enums;
using ToolBox.Factories;
using ToolBox.Models;

namespace ToolBox.Tools;

[McpServerToolType]
public class SqlAgent(IConfiguration configuration, IHttpContextAccessor httpContextAccessor)
{
	private readonly IConfiguration _configuration = configuration;
	private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;

	[McpServerTool, Description("Execute a query (supports join, where, where-date, where-in, where-string, group, having, combine, cte, order by, limit). don't use alias")]
	public async Task<string> ExecuteQuerySafe(
		[Description("The table name")]
		string tableName,
		[Description("List of columns to select")]
		List<SelectCondition> selectColumns,
		[Description("Dictionary of where columns and their values")]
		List<WhereCondition>? whereColumnsAndValues = null,
		[Description("Date-only where conditions handled by SqlKata WhereDate.")]
		List<DateWhereCondition>? dateWhereConditions = null,
		[Description("IN/NOT IN specific where conditions.")]
		List<InWhereCondition>? inWhereConditions = null,
		[Description("String matching where conditions (contains/starts/ends/like).")]
		List<StringWhereCondition>? stringWhereConditions = null,
		[Description("List of columns to order by. Each item can include 'Field', 'Aggregation' (e.g., COUNT, SUM), and 'Direction' (ASC or DESC).")]
		List<OrderByCondition>? orderByColumns = null,
		[Description("Limit the number of results returned")]
		int limit = 0,
		[Description("List of joins. Each join is a dictionary with keys: 'Table', 'On', and optional 'Type' (default 'INNER').")]
		List<JoinCondition>? joins = null,
		[Description("List of group by conditions. Each condition includes 'Table', 'Field'.")]
		List<GroupByCondition>? groupByConditions = null,
		[Description("List of having conditions.")]
		List<HavingCondition>? havingConditions = null,
		[Description("List of combine conditions (union/union all/intersect/except).")]
		List<CombineCondition>? combineConditions = null,
		[Description("List of CTE definitions.")]
		List<CteCondition>? cteConditions = null)
	{
		var sqlConfig = await ResolveSqlConfigAsync();
		if (!CheckProviderAndConnectionString(sqlConfig, out var dbType))
		{
			return $"Invalid provider or connection string: {sqlConfig.Provider} - {sqlConfig.ConnectionString}";
		}
		var strategy = SqlStrategyFactory.GetStrategy(dbType);
		var result = await strategy.ExecuteQueryAsync(
			sqlConfig.ConnectionString,
			tableName,
			selectColumns,
			whereColumnsAndValues,
			dateWhereConditions,
			inWhereConditions,
			stringWhereConditions,
			orderByColumns,
			groupByConditions,
			havingConditions,
			combineConditions,
			cteConditions,
			limit,
			joins
		);
		return result;
	}

	[McpServerTool, Description("Get column names of a table.")]
	public async Task<string> GetColumns(string tableName)
	{
		try
		{
			var sqlConfig = await ResolveSqlConfigAsync();
			if (!CheckProviderAndConnectionString(sqlConfig, out var dbType))
			{
				return $"Invalid provider or connection string: {sqlConfig.Provider} - {sqlConfig.ConnectionString}";
			}
			if (string.IsNullOrEmpty(tableName))
			{
				return "Table name cannot be empty. please provide a valid table name.";
			}
			var strategy = SqlStrategyFactory.GetStrategy(dbType);
			var columns = await strategy.GetColumnsAsync(sqlConfig.ConnectionString, tableName);
			return string.Join(", ", columns);
		}
		catch (Exception ex)
		{
			return $"Error getting columns: {ex.Message}";
		}
	}

	[McpServerTool, Description("Get list of schemas in the database.")]
	public async Task<string> GetSchemas()
	{
		try
		{
			var sqlConfig = await ResolveSqlConfigAsync();
			if (!CheckProviderAndConnectionString(sqlConfig, out var dbType))
			{
				return $"Invalid provider or connection string: {sqlConfig.Provider} - {sqlConfig.ConnectionString}";
			}
			var strategy = SqlStrategyFactory.GetStrategy(dbType);
			var schemas = await strategy.GetSchemasAsync(sqlConfig.ConnectionString);
			return string.Join(", ", schemas);
		}
		catch (Exception ex)
		{
			return $"Error getting schemas: {ex.Message}";
		}
	}

	[McpServerTool, Description("Get list of tables in the database.")]
	public async Task<string> GetTables()
	{
		try
		{
			var sqlConfig = await ResolveSqlConfigAsync();
			if (!CheckProviderAndConnectionString(sqlConfig, out var dbType))
			{
				return $"Invalid provider or connection string: {sqlConfig.Provider} - {sqlConfig.ConnectionString}";
			}
			var strategy = SqlStrategyFactory.GetStrategy(dbType);
			var tables = await strategy.GetTablesAsync(sqlConfig.ConnectionString);
			return string.Join(", ", tables);
		}
		catch (Exception ex)
		{
			return $"Error getting tables: {ex.Message}";
		}
	}

	[McpServerTool, Description("Get reference of a table.")]
	public async Task<string> GetTableReference([Description("The table name")] string tableName)
	{
		try
		{
			var sqlConfig = await ResolveSqlConfigAsync();
			if (!CheckProviderAndConnectionString(sqlConfig, out var dbType))
			{
				return $"Invalid provider or connection string: {sqlConfig.Provider} - {sqlConfig.ConnectionString}";
			}
			if (string.IsNullOrEmpty(tableName))
			{
				return "Table name cannot be empty. please provide a valid table name.";
			}
			var strategy = SqlStrategyFactory.GetStrategy(dbType);
			return await strategy.GetTableReferenceAsync(sqlConfig.ConnectionString, tableName);
		}
		catch (Exception ex)
		{
			return $"Error getting table reference: {ex.Message}";
		}
	}

	private static bool CheckProviderAndConnectionString(SqlRuntimeConfig sqlConfig, out SqlAgentToolType dbType)
	{
		dbType = default;
		if (string.IsNullOrEmpty(sqlConfig.Provider))
		{
			return false;
		}
		if (string.IsNullOrEmpty(sqlConfig.ConnectionString))
		{
			return false;
		}
		if (!Enum.TryParse(sqlConfig.Provider, true, out dbType))
		{
			return false;
		}
		return true;
	}

	private async Task<SqlRuntimeConfig> ResolveSqlConfigAsync()
	{
		var context = _httpContextAccessor.HttpContext;
		if (context is not null)
		{
			var provider = context.Items.TryGetValue(McpContextItemKeys.SqlProvider, out var providerObj)
				? providerObj?.ToString()
				: null;
			var connectionString = context.Items.TryGetValue(McpContextItemKeys.SqlConnectionString, out var connObj)
				? connObj?.ToString()
				: null;

			if (!string.IsNullOrWhiteSpace(provider) && !string.IsNullOrWhiteSpace(connectionString))
			{
				return new SqlRuntimeConfig
				{
					Provider = provider,
					ConnectionString = connectionString
				};
			}
		}

		return new SqlRuntimeConfig
		{
			Provider = _configuration["SqlConfig:Provider"] ?? string.Empty,
			ConnectionString = _configuration["SqlConfig:ConnectionString"] ?? string.Empty
		};
	}
}