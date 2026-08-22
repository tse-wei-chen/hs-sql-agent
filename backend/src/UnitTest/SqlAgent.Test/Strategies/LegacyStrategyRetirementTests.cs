using System.Reflection;
using Microsoft.Extensions.Configuration;
using Moq;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Models;
using SqlAgent.Service.Services;
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
    public async Task LegacyDmlExecution_IsFailClosedInsteadOfIssuingStringChallenge()
    {
        var strategy = CreateStrategy();

#pragma warning disable CS0618
        var error = await Assert.ThrowsAsync<NotSupportedException>(() =>
            strategy.ExecuteDmlAsync(
                "unused",
                new DmlDefinition(),
                CancellationToken.None));
#pragma warning restore CS0618

        Assert.Contains("TypedDmlRuntime", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("TokenRequired=", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfirmToken", error.Message, StringComparison.Ordinal);
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

    private static BaseSqlStrategy CreateStrategy()
    {
        var parser = new QueryValueParserService();
        var configuration = new Mock<IConfiguration>().Object;
        return new SqliteStrategy(parser, configuration);
    }
}
