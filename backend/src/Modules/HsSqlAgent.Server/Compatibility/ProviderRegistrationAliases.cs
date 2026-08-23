// Compile-time aliases keep the large service-registration method source-compatible while the
// runtime types have been retired. These aliases emit no ISqlStrategy/*Strategy/SqlStrategyFactory
// types; the DI registrations compile directly to the provider types below.
global using ISqlStrategy = SqlAgent.Service.Core.Providers.SqlProviderBase;
global using MySqlStrategy = SqlAgent.Service.Core.Providers.MySqlProvider;
global using PostgresStrategy = SqlAgent.Service.Core.Providers.PostgresProvider;
global using SqliteStrategy = SqlAgent.Service.Core.Providers.SqliteProvider;
global using MsSqlServerStrategy = SqlAgent.Service.Core.Providers.MsSqlServerProvider;
global using OracleStrategy = SqlAgent.Service.Core.Providers.OracleProvider;
global using FirebirdStrategy = SqlAgent.Service.Core.Providers.FirebirdProvider;
global using SqlStrategyFactory = SqlAgent.Service.Factories.SqlProviderFactory;

// HsSqlAgentServiceExtensions still imports the historical namespace. Keep an empty source
// namespace in this compilation until that monolithic registration file is split; it emits no type.
namespace SqlAgent.Service.Strategies
{
}
