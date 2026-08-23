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
        var mockDb1 = new Mock<ISqlStrategy>();
        mockDb1.Setup(x => x.DbType).Returns(SqlAgentToolType.Postgres);

        var mockDb2 = new Mock<ISqlStrategy>();
        mockDb2.Setup(x => x.DbType).Returns(SqlAgentToolType.Postgres);

        Assert.Throws<InvalidOperationException>(() =>
            new SqlStrategyFactory([mockDb1.Object, mockDb2.Object]));
    }

    [Fact]
    public void GetProvider_WithValidType_ReturnsCoreProvider()
    {
        var mockDb = new Mock<ISqlStrategy>();
        mockDb.Setup(x => x.DbType).Returns(SqlAgentToolType.Postgres);
        var factory = new SqlStrategyFactory([mockDb.Object]);

        var result = factory.GetProvider(SqlAgentToolType.Postgres);

        Assert.IsType<SqlProvider>(result);
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
        var mockDb1 = new Mock<ISqlStrategy>();
        mockDb1.Setup(x => x.DbType).Returns(SqlAgentToolType.Postgres);
        var mockDb2 = new Mock<ISqlStrategy>();
        mockDb2.Setup(x => x.DbType).Returns(SqlAgentToolType.MySQL);
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
        var mockDb = new Mock<ISqlStrategy>();
        mockDb.Setup(x => x.DbType).Returns(SqlAgentToolType.Postgres);
        mockDb.Setup(x => x.BuildConnectionString(model)).Returns("Host=db.example");
        var factory = new SqlStrategyFactory([mockDb.Object]);

        var result = factory.BuildConnectionString(SqlAgentToolType.Postgres, model);

        Assert.Equal("Host=db.example", result);
        mockDb.Verify(x => x.BuildConnectionString(model), Times.Once);
    }
}
