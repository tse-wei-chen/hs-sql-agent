using System.Reflection;
using SqlAgent.Service.Factories;
using Xunit;

namespace SqlAgent.Test.Providers;

public class ProviderRetirementArchitectureTests
{
    [Fact]
    public void LegacyStrategyRuntimeTypes_AreAbsent()
    {
        var assembly = typeof(SqlProviderBase).Assembly;
        var retiredTypes = new[]
        {
            "SqlAgent.Service.Strategies.ISqlStrategy",
            "SqlAgent.Service.Strategies.BaseSqlStrategy",
            "SqlAgent.Service.Strategies.PostgresStrategy",
            "SqlAgent.Service.Strategies.MySqlStrategy",
            "SqlAgent.Service.Strategies.SqliteStrategy",
            "SqlAgent.Service.Strategies.MsSqlServerStrategy",
            "SqlAgent.Service.Strategies.OracleStrategy",
            "SqlAgent.Service.Strategies.FirebirdStrategy",
            "SqlAgent.Service.Factories.SqlStrategyFactory"
        };

        foreach (var typeName in retiredTypes)
            Assert.Null(assembly.GetType(typeName, throwOnError: false));
    }

    [Fact]
    public void ProviderImplementations_LiveInDedicatedProviderAssembliesAndNamespaces()
    {
        var providerTypes = new[]
        {
            (typeof(PostgresProvider), "HsSqlAgent.Provider.PostgreSql", "HsSqlAgent.Provider.PostgreSql"),
            (typeof(MySqlProvider), "HsSqlAgent.Provider.MySql", "HsSqlAgent.Provider.MySql"),
            (typeof(SqliteProvider), "HsSqlAgent.Provider.Sqlite", "HsSqlAgent.Provider.Sqlite"),
            (typeof(MsSqlServerProvider), "HsSqlAgent.Provider.SqlServer", "HsSqlAgent.Provider.SqlServer"),
            (typeof(OracleProvider), "HsSqlAgent.Provider.Oracle", "HsSqlAgent.Provider.Oracle"),
            (typeof(FirebirdProvider), "HsSqlAgent.Provider.Firebird", "HsSqlAgent.Provider.Firebird")
        };

        foreach (var (providerType, namespaceName, assemblyName) in providerTypes)
        {
            Assert.Equal(namespaceName, providerType.Namespace);
            Assert.Equal(assemblyName, providerType.Assembly.GetName().Name);
            Assert.True(typeof(SqlProviderBase).IsAssignableFrom(providerType));
            Assert.True(typeof(ISqlProvider).IsAssignableFrom(providerType));
            var constructor = Assert.Single(providerType.GetConstructors(BindingFlags.Instance | BindingFlags.Public));
            Assert.Empty(constructor.GetParameters());
        }
    }

    [Fact]
    public void ProviderBase_HasNoLegacyCompilationExecutionOrAmbientSessionSurface()
    {
        var type = typeof(SqlProviderBase);
        var methods = type
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .ToArray();

        Assert.DoesNotContain("CompileQuerySql", methods);
        Assert.DoesNotContain("CompileQueryTranslation", methods);
        Assert.DoesNotContain("ExecuteQueryAsync", methods);
        Assert.DoesNotContain("ExecuteDmlAsync", methods);
        Assert.DoesNotContain("BuildExecutionErrorMessage", methods);
        Assert.Null(type.TypeInitializer);
        Assert.DoesNotContain(
            type.GetFields(BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
            field => IsAsyncLocal(field.FieldType));
    }

    [Fact]
    public void ProviderRuntimeCapabilities_AreExposedDirectly()
    {
        var provider = new PostgresProvider();
        ISqlProvider runtime = provider;

        Assert.Same(provider, runtime);
        Assert.Equal(SqlAgentToolType.Postgres, runtime.Type);
        Assert.Same(provider, runtime.Connections);
        Assert.Same(provider, runtime.Metadata);
        Assert.Equal(SqlAgentToolType.Postgres, runtime.Lowerer.Provider);
        Assert.IsType<ProviderExecutionErrorMapper>(runtime.Errors);
    }

    [Fact]
    public void ProviderFactory_HasNoStrategyNamedPublicSurface()
    {
        var methods = typeof(SqlProviderFactory).GetMethods(BindingFlags.Instance | BindingFlags.Public);
        Assert.DoesNotContain(methods, method => method.Name.Contains("Strategy", StringComparison.Ordinal));
    }

    private static bool IsAsyncLocal(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(AsyncLocal<>);
}
