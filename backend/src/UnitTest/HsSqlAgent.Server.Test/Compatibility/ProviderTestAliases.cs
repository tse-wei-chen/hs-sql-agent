// Compile-time aliases keep a few historical server-test assertions source-compatible while the
// production strategy runtime types remain retired. These aliases emit no legacy runtime types.
global using ISqlStrategy = SqlAgent.Service.Core.Providers.SqlProviderBase;
global using BaseSqlStrategy = SqlAgent.Service.Core.Providers.SqlProviderBase;

namespace SqlAgent.Service.Strategies
{
}
