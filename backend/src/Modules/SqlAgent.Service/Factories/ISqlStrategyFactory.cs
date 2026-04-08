using SqlAgent.Service.Enums;
using SqlAgent.Service.Strategies;

namespace SqlAgent.Service.Factories;

public interface ISqlStrategyFactory
{
	ISqlStrategy GetStrategy(SqlAgentToolType dbType);
	IEnumerable<SqlAgentToolType> GetSupportedDatabaseTypes();
}