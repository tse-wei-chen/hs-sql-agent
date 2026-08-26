using Moq;
using SqlAgent.Service.Factories;
using Xunit;

namespace SqlAgent.Test.Factories;

public class SqlProviderFactoryTests
{
    [Fact]
    public void Constructor_WithDuplicateProviders_ThrowsInvalidOperationException()
    {
        var provider1 = CreateProvider(SqlAgentToolType.Postgres);
        var provider2 = CreateProvider(SqlAgentToolType.Postgres);

        Assert.Throws<InvalidOperationException>(() =>
            new SqlProviderFactory([provider1.Object, provider2.Object]));
    }

    [Fact]
    public void GetProvider_WithValidType_ReturnsRegisteredProviderDirectly()
    {
        var provider = CreateProvider(SqlAgentToolType.Postgres);
        var factory = new SqlProviderFactory([provider.Object]);

        var result = factory.GetProvider(SqlAgentToolType.Postgres);

        Assert.Same(provider.Object, result);
        Assert.Equal(SqlAgentToolType.Postgres, result.Type);
    }

    [Fact]
    public void GetProvider_WithInvalidType_ThrowsArgumentOutOfRangeException()
    {
        var factory = new SqlProviderFactory([]);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            factory.GetProvider(SqlAgentToolType.Postgres));
    }

    [Fact]
    public void GetSupportedProviderTypes_ReturnsExpectedTypes()
    {
        var postgres = CreateProvider(SqlAgentToolType.Postgres);
        var mysql = CreateProvider(SqlAgentToolType.MySQL);
        var factory = new SqlProviderFactory([postgres.Object, mysql.Object]);

        var types = factory.GetSupportedProviderTypes();

        Assert.Contains(SqlAgentToolType.Postgres, types);
        Assert.Contains(SqlAgentToolType.MySQL, types);
        Assert.Equal(2, types.Count);
    }

    [Fact]
    public void BuildConnectionString_DelegatesToProviderManagementSurface()
    {
        var model = new BuildDbConnectionModelBase { Host = "db.example" };
        var provider = CreateProvider(SqlAgentToolType.Postgres);
        provider.Setup(x => x.BuildConnectionString(model)).Returns("Host=db.example");
        var factory = new SqlProviderFactory([provider.Object]);

        var result = factory.BuildConnectionString(SqlAgentToolType.Postgres, model);

        Assert.Equal("Host=db.example", result);
        provider.Verify(x => x.BuildConnectionString(model), Times.Once);
    }

    [Fact]
    public void FactoryContracts_ExposeOnlyProviderAndConnectionStringBoundaries()
    {
        var interfaces = typeof(SqlProviderFactory).GetInterfaces();
        var methods = typeof(SqlProviderFactory).GetMethods();

        Assert.Contains(typeof(ISqlProviderFactory), interfaces);
        Assert.Contains(typeof(ISqlConnectionStringFactory), interfaces);
        Assert.DoesNotContain(methods, method => method.Name.Contains("Strategy", StringComparison.Ordinal));
    }

    private static Mock<SqlProviderBase> CreateProvider(SqlAgentToolType type)
    {
        var provider = new Mock<SqlProviderBase>();
        provider.SetupGet(x => x.DbType).Returns(type);
        return provider;
    }
}
