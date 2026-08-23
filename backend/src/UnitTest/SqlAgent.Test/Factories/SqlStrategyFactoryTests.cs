using Moq;
using SqlAgent.Service.Core.Providers;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Factories;
using SqlAgent.Service.Models;
using SqlAgent.Service.Strategies;
using Xunit;

namespace SqlAgent.Test.Factories;

public class SqlStrategyFactoryTests
{
    [Fact]
    public void Constructor_WithDuplicateStrategies_ThrowsInvalidOperationException()
    {
        var mockDb1 = CreateProvider(SqlAgentToolType.Postgres);
        var mockDb2 = CreateProvider(SqlAgentToolType.Postgres);

        Assert.Throws<InvalidOperationException>(() =>
            new SqlStrategyFactory([mockDb1.Object, mockDb2.Object]));
    }

    [Fact]
    public void Constructor_WithNonProviderStrategy_ThrowsInvalidOperationException()
    {
        var registration = new Mock<ISqlStrategy>();
        registration.SetupGet(x => x.DbType).Returns(SqlAgentToolType.Postgres);

        var error = Assert.Throws<InvalidOperationException>(() =>
            new SqlStrategyFactory([registration.Object]));

        Assert.Contains(nameof(ISqlProvider), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetProvider_WithValidType_ReturnsRegisteredProviderDirectly()
    {
        var mockDb = CreateProvider(SqlAgentToolType.Postgres);
        var factory = new SqlStrategyFactory([mockDb.Object]);

        var result = factory.GetProvider(SqlAgentToolType.Postgres);

        Assert.Same(mockDb.Object, result);
        Assert.Equal(SqlAgentToolType.Postgres, result.Type);
    }

    [Fact]
    public void GetProvider_WithInvalidType_ThrowsArgumentOutOfRangeException()
    {
        var factory = new SqlStrategyFactory([]);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            factory.GetProvider(SqlAgentToolType.Postgres));
    }

    [Fact]
    public void GetSupportedProviderTypes_ReturnsExpectedTypes()
    {
        var mockDb1 = CreateProvider(SqlAgentToolType.Postgres);
        var mockDb2 = CreateProvider(SqlAgentToolType.MySQL);
        var factory = new SqlStrategyFactory([mockDb1.Object, mockDb2.Object]);

        var types = factory.GetSupportedProviderTypes();

        Assert.Contains(SqlAgentToolType.Postgres, types);
        Assert.Contains(SqlAgentToolType.MySQL, types);
        Assert.Equal(2, types.Count);
    }

    [Fact]
    public void BuildConnectionString_DelegatesWithoutExposingStrategy()
    {
        var model = new BuildDbConnectionModelBase { Host = "db.example" };
        var mockDb = CreateProvider(SqlAgentToolType.Postgres);
        mockDb.Setup(x => x.BuildConnectionString(model)).Returns("Host=db.example");
        var factory = new SqlStrategyFactory([mockDb.Object]);

        var result = factory.BuildConnectionString(SqlAgentToolType.Postgres, model);

        Assert.Equal("Host=db.example", result);
        mockDb.Verify(x => x.BuildConnectionString(model), Times.Once);
    }

    [Fact]
    public void FactoryContracts_AreExplicitAndDoNotExposeStrategies()
    {
        var interfaces = typeof(SqlStrategyFactory).GetInterfaces();
        var implementationMethods = typeof(SqlStrategyFactory).GetMethods();

        Assert.Contains(typeof(ISqlProviderFactory), interfaces);
        Assert.Contains(typeof(ISqlConnectionStringFactory), interfaces);
        Assert.DoesNotContain(implementationMethods, method => method.Name == "GetStrategy");
        Assert.DoesNotContain(implementationMethods, method => method.Name == "GetSupportedDatabaseTypes");
        Assert.DoesNotContain(implementationMethods, method => typeof(ISqlStrategy).IsAssignableFrom(method.ReturnType));
    }

    private static Mock<BaseSqlStrategy> CreateProvider(SqlAgentToolType type)
    {
        var provider = new Mock<BaseSqlStrategy>();
        provider.SetupGet(x => x.DbType).Returns(type);
        return provider;
    }
}
