using System.Reflection;
using ToolBox.Enums;
using ToolBox.Strategies;

namespace ToolBox.Factories;

public static class SqlStrategyFactory
{
	private static readonly Dictionary<SqlAgentToolType, ISqlStrategy> _strategies = new();

	static SqlStrategyFactory()
	{
		var strategyType = typeof(ISqlStrategy);
		var assembly = Assembly.GetExecutingAssembly();

		var strategies = assembly.GetTypes()
			.Where(t => t.IsClass && !t.IsAbstract && strategyType.IsAssignableFrom(t))
			.ToList();

		foreach (var type in strategies)
		{
			if (Activator.CreateInstance(type) is ISqlStrategy instance)
			{
				_strategies[instance.DbType] = instance;
			}
		}
	}

	public static ISqlStrategy GetStrategy(SqlAgentToolType dbType)
	{
		if (_strategies.TryGetValue(dbType, out var strategy))
		{
			return strategy;
		}

		throw new ArgumentOutOfRangeException(nameof(dbType), dbType, $"No strategy found for database type: {dbType}");
	}

	public static IEnumerable<SqlAgentToolType> GetSupportedDatabaseTypes() => _strategies.Keys;
}