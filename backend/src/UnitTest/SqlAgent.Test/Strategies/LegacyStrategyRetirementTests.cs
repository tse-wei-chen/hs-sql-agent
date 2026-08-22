using System.Reflection;
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
    public void BaseStrategy_HasNoLegacyCompilationOrExecutionSurface()
    {
        var methods = typeof(BaseSqlStrategy)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .ToArray();

        Assert.DoesNotContain("CompileQuerySql", methods);
        Assert.DoesNotContain("CompileQueryTranslation", methods);
        Assert.DoesNotContain("ExecuteQueryAsync", methods);
        Assert.DoesNotContain("ExecuteDmlAsync", methods);
    }

    [Fact]
    public void ProviderStrategies_DoNotOwnSqlKataCompilerFactories()
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
            Assert.Null(providerType.GetMethod(
                "CreateCompiler",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly));
        }
    }

    private static bool IsAsyncLocal(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(AsyncLocal<>);
}
