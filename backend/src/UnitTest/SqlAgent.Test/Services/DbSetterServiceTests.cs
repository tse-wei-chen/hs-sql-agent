using System.Data.Common;
using Moq;
using SqlAgent.Service.Factories;
using SqlAgent.Service.Services;
using Xunit;

namespace SqlAgent.Test.Services;

public class DbSetterServiceTests
{
    private readonly Mock<ISqlProviderFactory> _providerFactoryMock;
    private readonly Mock<ISqlConnectionStringFactory> _connectionStringFactoryMock;
    private readonly DbSetterService _service;

    public DbSetterServiceTests()
    {
        _providerFactoryMock = new Mock<ISqlProviderFactory>();
        _connectionStringFactoryMock = new Mock<ISqlConnectionStringFactory>();
        _service = new DbSetterService(
            _providerFactoryMock.Object,
            _connectionStringFactoryMock.Object);
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
        var request = new TestDbConnectionBase { SqlProvider = null };

        var result = await _service.TestDbConnectionAsync(request, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("Provider is null.", result.ErrorMessage);
    }

    [Fact]
    public async Task TestDbConnectionAsync_ShouldReturnSuccess_WhenConnectionOpensSuccessfully()
    {
        var request = new TestDbConnectionBase
        {
            SqlProvider = SqlAgentToolType.Postgres,
            Host = "localhost",
            Database = "test_db"
        };
        var connString = "Host=localhost;Database=test_db;";
        _connectionStringFactoryMock
            .Setup(f => f.BuildConnectionString(
                SqlAgentToolType.Postgres,
                It.Is<BuildDbConnectionModelBase>(m =>
                    m.Host == request.Host &&
                    m.Database == request.Database)))
            .Returns(connString);

        var mockConnection = CreateMockDbConnection();
        var connectionFactory = new Mock<IDbConnectionFactory>();
        connectionFactory.Setup(f => f.Create(connString)).Returns(mockConnection.Object);
        var provider = new Mock<ISqlProvider>();
        provider.SetupGet(p => p.Connections).Returns(connectionFactory.Object);
        _providerFactoryMock.Setup(f => f.GetProvider(SqlAgentToolType.Postgres)).Returns(provider.Object);

        var result = await _service.TestDbConnectionAsync(request, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Null(result.ErrorMessage);
        _connectionStringFactoryMock.Verify(f => f.BuildConnectionString(
            SqlAgentToolType.Postgres,
            It.Is<BuildDbConnectionModelBase>(m =>
                m.Host == request.Host &&
                m.Database == request.Database)), Times.Once);
        connectionFactory.Verify(f => f.Create(connString), Times.Once);
        mockConnection.Verify(c => c.OpenAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TestDbConnectionAsync_ShouldReturnError_WhenConnectionThrowsException()
    {
        var request = new TestDbConnectionBase { SqlProvider = SqlAgentToolType.MySQL };
        var connString = "Server=localhost;";
        _connectionStringFactoryMock
            .Setup(f => f.BuildConnectionString(
                SqlAgentToolType.MySQL,
                It.IsAny<BuildDbConnectionModelBase>()))
            .Returns(connString);

        var mockConnection = CreateMockDbConnection(throwOnOpen: true, errorMessage: "Access denied");
        var connectionFactory = new Mock<IDbConnectionFactory>();
        connectionFactory.Setup(f => f.Create(connString)).Returns(mockConnection.Object);
        var provider = new Mock<ISqlProvider>();
        provider.SetupGet(p => p.Connections).Returns(connectionFactory.Object);
        _providerFactoryMock.Setup(f => f.GetProvider(SqlAgentToolType.MySQL)).Returns(provider.Object);

        var result = await _service.TestDbConnectionAsync(request, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("Access denied", result.ErrorMessage);
        connectionFactory.Verify(f => f.Create(connString), Times.Once);
        mockConnection.Verify(c => c.OpenAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BuildDbConnectionAsync_ShouldReturnConnectionString()
    {
        var model = new BuildDbConnectionModel { Provider = "MsSqlServer" };
        var expectedConnString = "Server=localhost;Database=db;";
        _connectionStringFactoryMock
            .Setup(f => f.BuildConnectionString(SqlAgentToolType.MsSqlServer, model))
            .Returns(expectedConnString);

        var result = await _service.BuildDbConnectionAsync(model, TestContext.Current.CancellationToken);

        Assert.Equal(expectedConnString, result);
        _connectionStringFactoryMock.Verify(
            f => f.BuildConnectionString(SqlAgentToolType.MsSqlServer, model),
            Times.Once);
    }
}
