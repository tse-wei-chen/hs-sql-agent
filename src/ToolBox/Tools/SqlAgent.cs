
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using ToolBox.Enums;
using ToolBox.Factories;
using ToolBox.Models;

namespace ToolBox.Tools;

[McpServerToolType]
public class SqlAgent(SqlConfig dbCtx)
{
	private readonly SqlConfig _dbCtx = dbCtx;

    [McpServerTool, Description("Execute a query (supports join, where, order by, limit). don't use alias")]
	public async Task<string> ExecuteQuerySafe(
		[Description("The table name")]
		string tableName,
		[Description("List of columns to select")]
		List<SelectCondition> selectColumns,
		[Description("Dictionary of where columns and their values")]
		List<WhereCondition>? whereColumnsAndValues = null,
		[Description("List of columns to order by. Each item can include 'Field', 'Aggregation' (e.g., COUNT, SUM), and 'Direction' (ASC or DESC).")]
		List<OrderByCondition>? orderByColumns = null,
		[Description("Limit the number of results returned")]
		int limit = 0,
		[Description("List of joins. Each join is a dictionary with keys: 'Table', 'On', and optional 'Type' (default 'INNER').")]
		List<JoinCondition>? joins = null,
		[Description("List of group by conditions. Each condition includes 'Table', 'Field'.")]
		List<GroupByCondition>? groupByConditions = null)
	{
		if (!CheckProviderAndConnectionString(out var dbType))
		{
			return $"Invalid provider or connection string: {_dbCtx.Provider} - {_dbCtx.ConnectionString}";
		}
		var strategy = SqlStrategyFactory.GetStrategy(dbType);
		var result = await strategy.ExecuteQueryAsync(
			_dbCtx.ConnectionString ?? "",
			tableName,
			selectColumns,
			whereColumnsAndValues,
			orderByColumns,
			groupByConditions,
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
			if (!CheckProviderAndConnectionString(out var dbType))
			{
				return $"Invalid provider or connection string: {_dbCtx.Provider} - {_dbCtx.ConnectionString}";
			}
			if (string.IsNullOrEmpty(tableName))
			{
				return "Table name cannot be empty. please provide a valid table name.";
			}
			var strategy = SqlStrategyFactory.GetStrategy(dbType);
			var columns = await strategy.GetColumnsAsync(_dbCtx.ConnectionString ?? "", tableName);
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
			if (!CheckProviderAndConnectionString(out var dbType))
			{
				return $"Invalid provider or connection string: {_dbCtx.Provider} - {_dbCtx.ConnectionString}";
			}
			var strategy = SqlStrategyFactory.GetStrategy(dbType);
			var schemas = await strategy.GetSchemasAsync(_dbCtx.ConnectionString ?? "");
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
			if (!CheckProviderAndConnectionString(out var dbType))
			{
				return $"Invalid provider or connection string: {_dbCtx.Provider} - {_dbCtx.ConnectionString}";
			}
			var strategy = SqlStrategyFactory.GetStrategy(dbType);
			var tables = await strategy.GetTablesAsync(_dbCtx.ConnectionString ?? "");
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
			if (!CheckProviderAndConnectionString(out var dbType))
			{
				return $"Invalid provider or connection string: {_dbCtx.Provider} - {_dbCtx.ConnectionString}";
			}
			if (string.IsNullOrEmpty(tableName))
			{
				return "Table name cannot be empty. please provide a valid table name.";
			}
			var strategy = SqlStrategyFactory.GetStrategy(dbType);
			return await strategy.GetTableReferenceAsync(_dbCtx.ConnectionString ?? "", tableName);
		}
		catch (Exception ex)
		{
			return $"Error getting table reference: {ex.Message}";
		}
	}

	private bool CheckProviderAndConnectionString(out SqlAgentToolType dbType)
	{
		dbType = default;
		if (string.IsNullOrEmpty(_dbCtx.Provider))
		{
			return false;
		}
		if (string.IsNullOrEmpty(_dbCtx.ConnectionString))
		{
			return false;
		}
		if (!Enum.TryParse(_dbCtx.Provider, true, out dbType))
		{
			return false;
		}
		return true;
	}
}