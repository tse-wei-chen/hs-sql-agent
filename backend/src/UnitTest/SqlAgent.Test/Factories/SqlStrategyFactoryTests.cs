using Moq;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Factories;
using SqlAgent.Service.Interfaces;
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

		var strategies = new List<ISqlStrategy> { mockDb1.Object, mockDb2.Object };

		Assert.Throws<InvalidOperationException>(() => new SqlStrategyFactory(strategies));
	}

	[Fact]
	public void GetStrategy_WithValidType_ReturnsStrategy()
	{
		var mockDb = new Mock<ISqlStrategy>();
		mockDb.Setup(x => x.DbType).Returns(SqlAgentToolType.Postgres);

		var factory = new SqlStrategyFactory(new[] { mockDb.Object });

		var result = factory.GetStrategy(SqlAgentToolType.Postgres);

		Assert.Equal(mockDb.Object, result);
	}

	[Fact]
	public void GetStrategy_WithInvalidType_ThrowsArgumentOutOfRangeException()
	{
		var factory = new SqlStrategyFactory(Array.Empty<ISqlStrategy>());

		Assert.Throws<ArgumentOutOfRangeException>(() => factory.GetStrategy(SqlAgentToolType.Postgres));
	}

	[Fact]
	public void GetSupportedDatabaseTypes_ReturnsExpectedTypes()
	{
		var mockDb1 = new Mock<ISqlStrategy>();
		mockDb1.Setup(x => x.DbType).Returns(SqlAgentToolType.Postgres);
		var mockDb2 = new Mock<ISqlStrategy>();
		mockDb2.Setup(x => x.DbType).Returns(SqlAgentToolType.MySQL);

		var factory = new SqlStrategyFactory(new[] { mockDb1.Object, mockDb2.Object });

		var types = factory.GetSupportedDatabaseTypes().ToList();

		Assert.Contains(SqlAgentToolType.Postgres, types);
		Assert.Contains(SqlAgentToolType.MySQL, types);
		Assert.Equal(2, types.Count);
	}
}
