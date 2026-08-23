// Provider integration fixtures keep their historical test names to avoid rewriting large
// database-specific suites. These are compile-time aliases only; production no longer exposes
// strategy runtime types.
global using ISqlStrategy = SqlAgent.Service.Core.Providers.SqlProviderBase;
global using BaseSqlStrategy = SqlAgent.Service.Core.Providers.SqlProviderBase;
global using MySqlStrategy = SqlAgent.Service.Core.Providers.MySqlProvider;
global using PostgresStrategy = SqlAgent.Service.Core.Providers.PostgresProvider;
global using SqliteStrategy = SqlAgent.Service.Core.Providers.SqliteProvider;
global using MsSqlServerStrategy = SqlAgent.Service.Core.Providers.MsSqlServerProvider;
global using OracleStrategy = SqlAgent.Service.Core.Providers.OracleProvider;
global using FirebirdStrategy = SqlAgent.Service.Core.Providers.FirebirdProvider;
global using SqlStrategyFactory = SqlAgent.Service.Factories.SqlProviderFactory;
global using ISqlStrategyFactory = SqlAgent.Service.Core.Providers.ISqlProviderFactory;

namespace SqlAgent.Service.Strategies
{
}
