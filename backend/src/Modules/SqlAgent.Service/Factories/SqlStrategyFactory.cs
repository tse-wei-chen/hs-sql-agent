using SqlAgent.Service.Enums;
using SqlAgent.Service.Strategies;

namespace SqlAgent.Service.Factories;

public class SqlStrategyFactory : ISqlStrategyFactory
{
	private readonly IReadOnlyDictionary<SqlAgentToolType, ISqlStrategy> _strategies;

	public SqlStrategyFactory(IEnumerable<ISqlStrategy> strategies)
	{
		var map = new Dictionary<SqlAgentToolType, ISqlStrategy>();
		foreach (var strategy in strategies)
		{
			if (!map.TryAdd(strategy.DbType, strategy))
			{
				throw new InvalidOperationException($"Duplicate strategy registration for database type: {strategy.DbType}");
			}
		}

		_strategies = map;
	}

	public ISqlStrategy GetStrategy(SqlAgentToolType dbType)
	{
		if (_strategies.TryGetValue(dbType, out var strategy))
		{
			return strategy;
		}

		throw new ArgumentOutOfRangeException(nameof(dbType), dbType, $"No strategy found for database type: {dbType}");
	}

	public IEnumerable<SqlAgentToolType> GetSupportedDatabaseTypes() => _strategies.Keys;
}