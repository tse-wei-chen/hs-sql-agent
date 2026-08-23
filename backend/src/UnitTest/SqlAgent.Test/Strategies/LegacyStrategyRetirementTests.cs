using System.Reflection;
using SqlAgent.Service.Core.Providers;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Strategies;
using Xunit;

namespace SqlAgent.Test.Strategies;

public class LegacyStrategyRetirementTests
{
    [Fact]
    public void BaseStrategy_HasNoAmbientTranslationSession()
    {
        var type = typeof(BaseSqlStrategy);

        Assert.DoesNotContain(
            type.GetFields(BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
            field => IsAsyncLocal(field.FieldType));
        Assert.DoesNotContain(
            type.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public),
            nested => nested.Name.Contains("TranslationSession", StringComparison.Ordinal));
    }

    [Fact]
    public void BaseStrategy_HasNoLegacyCompilationExecutionOrInitializationSurface()
    {
        var type = typeof(BaseSqlStrategy);
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
    }

    [Fact]
    public void ProviderStrategies_AreParameterlessAndDoNotOwnSqlKataOrErrorFormattingHooks()
    {
        var providerTypes = new[]
        {
            typeof(PostgresStrategy),
            typeof(MySqlStrategy),
            typeof(MsSqlServerStrategy),
            typeof(SqliteStrategy),
            typeof(OracleStrategy),
            typeof(FirebirdStrategy)
        };

        foreach (var providerType in providerTypes)
        {
            var constructors = providerType.GetConstructors(BindingFlags.Instance | BindingFlags.Public);
            var constructor = Assert.Single(constructors);
            Assert.Empty(constructor.GetParameters());
            Assert.Null(providerType.GetMethod(
                "CreateCompiler",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly));
            Assert.Null(providerType.GetMethod(
                "BuildExecutionErrorMessage",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly));
        }
    }

    [Fact]
    public void ProviderStrategy_ImplementsCoreRuntimeCapabilitiesDirectly()
    {
        var strategy = new PostgresStrategy();
        var provider = Assert.IsAssignableFrom<ISqlProvider>(strategy);

        Assert.Same(strategy, provider);
        Assert.Equal(SqlAgentToolType.Postgres, provider.Type);
        Assert.Same(strategy, provider.Connections);
        Assert.Same(strategy, provider.Metadata);
        Assert.Equal(SqlAgentToolType.Postgres, provider.Lowerer.Provider);
    }

    [Fact]
    public void ProviderErrorMapper_ReturnsTypedProviderExecutionException()
    {
        var mapped = new ProviderExecutionErrorMapper(SqlAgentToolType.Postgres)
            .Map(new InvalidOperationException("boom"), "query");

        var error = Assert.IsType<ProviderExecutionException>(mapped);
        Assert.Equal(SqlAgentToolType.Postgres, error.ProviderType);
        Assert.Equal("query", error.Operation);
        Assert.Equal("unknown", error.Code);
        Assert.Equal("boom", error.ProviderMessage);
    }

    private static bool IsAsyncLocal(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(AsyncLocal<>);
}
