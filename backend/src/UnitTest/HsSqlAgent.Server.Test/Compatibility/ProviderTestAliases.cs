global using IDbConnectionFactory = HsSqlAgent.Provider.Abstractions.IDbConnectionFactory;
global using IDmlPreviewTransactionFactory = HsSqlAgent.Provider.Abstractions.IDmlPreviewTransactionFactory;
global using IProviderDmlPreviewTransactionSource = HsSqlAgent.Provider.Abstractions.IProviderDmlPreviewTransactionSource;
global using ProviderDmlPreviewTransactionFactory = HsSqlAgent.Provider.Abstractions.ProviderDmlPreviewTransactionFactory;
global using ISqlProvider = HsSqlAgent.Provider.Abstractions.ISqlProvider;
global using ISqlProviderFactory = HsSqlAgent.Provider.Abstractions.ISqlProviderFactory;
global using SqlProviderBase = HsSqlAgent.Provider.Abstractions.SqlProviderBase;
global using ProviderExecutionException = HsSqlAgent.Provider.Abstractions.ProviderExecutionException;
global using PostgresProvider = HsSqlAgent.Provider.PostgreSql.PostgresProvider;
global using MySqlProvider = HsSqlAgent.Provider.MySql.MySqlProvider;
global using SqliteProvider = HsSqlAgent.Provider.Sqlite.SqliteProvider;
global using MsSqlServerProvider = HsSqlAgent.Provider.SqlServer.MsSqlServerProvider;
global using OracleProvider = HsSqlAgent.Provider.Oracle.OracleProvider;
global using FirebirdProvider = HsSqlAgent.Provider.Firebird.FirebirdProvider;
global using FirebirdDmlPreviewTransactionFactory = HsSqlAgent.Provider.Firebird.FirebirdDmlPreviewTransactionFactory;

global using ISqlStrategy = HsSqlAgent.Provider.Abstractions.SqlProviderBase;
global using BaseSqlStrategy = HsSqlAgent.Provider.Abstractions.SqlProviderBase;

namespace SqlAgent.Service.Core.Providers { internal static class ProviderNamespaceCompatibilityMarker; }
namespace SqlAgent.Service.Strategies { }
