using SqlAgent.Service.Core.Execution;
using SqlAgent.Service.Core.Providers;
using Xunit;

namespace SqlAgent.Test.Providers;

public sealed class ProviderPackageBoundaryTests
{
    private static readonly string[] DriverAssemblies =
    [
        "Npgsql",
        "MySql.Data",
        "Microsoft.Data.Sqlite",
        "Microsoft.Data.SqlClient",
        "Oracle.ManagedDataAccess",
        "FirebirdSql.Data.FirebirdClient"
    ];

    [Fact]
    public void ProviderContracts_AreOwnedByDriverFreeAbstractionsAssembly()
    {
        var assembly = typeof(ISqlProvider).Assembly;

        Assert.Equal("HsSqlAgent.Provider.Abstractions", assembly.GetName().Name);
        Assert.Same(assembly, typeof(SqlProviderBase).Assembly);
        Assert.Same(assembly, typeof(SqlProvider).Assembly);
        Assert.Same(assembly, typeof(ProviderExecutionErrorMapper).Assembly);
        Assert.Same(assembly, typeof(IDbConnectionFactory).Assembly);
        Assert.Same(assembly, typeof(IDmlPreviewTransactionFactory).Assembly);
        Assert.Same(assembly, typeof(ProviderDmlPreviewTransactionFactory).Assembly);

        var references = ReferenceNames(typeof(ISqlProvider));
        Assert.Contains("HsSqlAgent.SqlCore", references);
        Assert.DoesNotContain("Dapper", references);
        foreach (var driver in DriverAssemblies)
            Assert.DoesNotContain(driver, references);
    }

    [Fact]
    public void ConcreteProviders_AreIsolatedIntoOneDriverPerAssembly()
    {
        var providers = new[]
        {
            new ProviderCase(typeof(PostgresProvider), "HsSqlAgent.Provider.PostgreSql", "Npgsql"),
            new ProviderCase(typeof(MySqlProvider), "HsSqlAgent.Provider.MySql", "MySql.Data"),
            new ProviderCase(typeof(SqliteProvider), "HsSqlAgent.Provider.Sqlite", "Microsoft.Data.Sqlite"),
            new ProviderCase(typeof(MsSqlServerProvider), "HsSqlAgent.Provider.SqlServer", "Microsoft.Data.SqlClient"),
            new ProviderCase(typeof(OracleProvider), "HsSqlAgent.Provider.Oracle", "Oracle.ManagedDataAccess"),
            new ProviderCase(typeof(FirebirdProvider), "HsSqlAgent.Provider.Firebird", "FirebirdSql.Data.FirebirdClient")
        };

        foreach (var provider in providers)
        {
            Assert.Equal(provider.AssemblyName, provider.Type.Assembly.GetName().Name);

            var references = ReferenceNames(provider.Type);
            Assert.Contains("HsSqlAgent.Provider.Abstractions", references);
            Assert.Contains("Dapper", references);
            Assert.Contains(provider.DriverAssembly, references);

            foreach (var otherDriver in DriverAssemblies.Where(
                         driver => !string.Equals(driver, provider.DriverAssembly, StringComparison.OrdinalIgnoreCase)))
                Assert.DoesNotContain(otherDriver, references);
        }
    }

    [Fact]
    public void FirebirdPreviewSafety_IsOwnedByFirebirdProviderAssembly()
    {
        Assert.Equal(
            "HsSqlAgent.Provider.Firebird",
            typeof(FirebirdDmlPreviewTransactionFactory).Assembly.GetName().Name);
        Assert.Same(
            typeof(FirebirdProvider).Assembly,
            typeof(FirebirdDmlPreviewTransactionFactory).Assembly);
    }

    [Fact]
    public void SqlAgentService_NoLongerOwnsProviderRuntimeTypesOrReferencesDatabaseDrivers()
    {
        var serviceAssembly = typeof(SqlAgent.Service.Factories.SqlProviderFactory).Assembly;

        Assert.Equal("SqlAgent.Service", serviceAssembly.GetName().Name);
        Assert.Null(serviceAssembly.GetType("SqlAgent.Service.Core.Providers.ISqlProvider", throwOnError: false));
        Assert.Null(serviceAssembly.GetType("SqlAgent.Service.Core.Providers.SqlProviderBase", throwOnError: false));
        Assert.Null(serviceAssembly.GetType("SqlAgent.Service.Core.Providers.PostgresProvider", throwOnError: false));
        Assert.Null(serviceAssembly.GetType("SqlAgent.Service.Core.Providers.MySqlProvider", throwOnError: false));
        Assert.Null(serviceAssembly.GetType("SqlAgent.Service.Core.Providers.SqliteProvider", throwOnError: false));
        Assert.Null(serviceAssembly.GetType("SqlAgent.Service.Core.Providers.MsSqlServerProvider", throwOnError: false));
        Assert.Null(serviceAssembly.GetType("SqlAgent.Service.Core.Providers.OracleProvider", throwOnError: false));
        Assert.Null(serviceAssembly.GetType("SqlAgent.Service.Core.Providers.FirebirdProvider", throwOnError: false));

        var references = serviceAssembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Dapper", references);
        foreach (var driver in DriverAssemblies)
            Assert.DoesNotContain(driver, references);
    }

    private static HashSet<string> ReferenceNames(Type type) =>
        type.Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private sealed record ProviderCase(Type Type, string AssemblyName, string DriverAssembly);
}
