using System.Data.Common;
using Moq;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Factories;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Models;
using SqlAgent.Service.Services;
using SqlAgent.Service.Strategies;
using Xunit;

namespace SqlAgent.Test.Services;

public class DbSetterServiceTests
{
    private readonly Mock<ISqlStrategyFactory> _strategyFactoryMock;
    private readonly DbSetterService _service;

    public DbSetterServiceTests()
    {
        _strategyFactoryMock = new Mock<ISqlStrategyFactory>();
        _service = new DbSetterService(_strategyFactoryMock.Object);
    }

    private Mock<DbConnection> CreateMockDbConnection(bool throwOnOpen = false, string errorMessage = "Connection failed")
    {
        var mockConnection = new Mock<DbConnection>();
        if (throwOnOpen)
        {
            mockConnection.Setup(c => c.OpenAsync(It.IsAny<CancellationToken>()))
                          .ThrowsAsync(new Exception(errorMessage));
        }
        else
        {
            mockConnection.Setup(c => c.OpenAsync(It.IsAny<CancellationToken>()))
                          .Returns(Task.CompletedTask);
        }
        return mockConnection;
    }

    [Fact]
    public async Task TestDbConnectionAsync_ShouldReturnError_WhenProviderIsNull()
    {
        // Arrange
        var request = new TestDbConnectionBase { SqlProvider = null };

        // Act
        var result = await _service.TestDbConnectionAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("Provider is null.", result.ErrorMessage);
    }

    [Fact]
    public async Task TestDbConnectionAsync_ShouldReturnSuccess_WhenConnectionOpensSuccessfully()
    {
        // Arrange
        var request = new TestDbConnectionBase
        {
            SqlProvider = SqlAgentToolType.Postgres,
            Host = "localhost",
            Database = "test_db"
        };

        var connString = "Host=localhost;Database=test_db;";
        var mockStrategy = new Mock<ISqlStrategy>();
        mockStrategy.Setup(s => s.BuildConnectionString(It.IsAny<BuildDbConnectionModelBase>())).Returns(connString);

        var mockConnection = CreateMockDbConnection();
        mockStrategy.Setup(s => s.CreateConnection(connString)).Returns(mockConnection.Object);

        _strategyFactoryMock.Setup(f => f.GetStrategy(SqlAgentToolType.Postgres)).Returns(mockStrategy.Object);

        // Act
        var result = await _service.TestDbConnectionAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Null(result.ErrorMessage);
        mockStrategy.Verify(s => s.BuildConnectionString(It.Is<BuildDbConnectionModelBase>(m =>
            m.Host == request.Host &&
            m.Database == request.Database)), Times.Once);
        mockConnection.Verify(c => c.OpenAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TestDbConnectionAsync_ShouldReturnError_WhenConnectionThrowsException()
    {
        // Arrange
        var request = new TestDbConnectionBase { SqlProvider = SqlAgentToolType.MySQL };
        var connString = "Server=localhost;";

        var mockStrategy = new Mock<ISqlStrategy>();
        mockStrategy.Setup(s => s.BuildConnectionString(It.IsAny<BuildDbConnectionModelBase>())).Returns(connString);

        var mockConnection = CreateMockDbConnection(throwOnOpen: true, errorMessage: "Access denied");
        mockStrategy.Setup(s => s.CreateConnection(connString)).Returns(mockConnection.Object);

        _strategyFactoryMock.Setup(f => f.GetStrategy(SqlAgentToolType.MySQL)).Returns(mockStrategy.Object);

        // Act
        var result = await _service.TestDbConnectionAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("Access denied", result.ErrorMessage);
        mockConnection.Verify(c => c.OpenAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BuildDbConnectionAsync_ShouldReturnConnectionString()
    {
        // Arrange
        var providerStr = "MsSqlServer";
        var model = new BuildDbConnectionModel { Provider = providerStr };
        var expectedConnString = "Server=localhost;Database=db;";

        var mockStrategy = new Mock<ISqlStrategy>();
        mockStrategy.Setup(s => s.BuildConnectionString(model)).Returns(expectedConnString);

        _strategyFactoryMock.Setup(f => f.GetStrategy(SqlAgentToolType.MsSqlServer)).Returns(mockStrategy.Object);

        // Act
        var result = await _service.BuildDbConnectionAsync(model, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expectedConnString, result);
    }
}
