using Admin.Service.Interfaces;
using Admin.Service.Models;
using HsSqlAgent.Server.Controllers;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace HsSqlAgent.Server.Test.Controllers;

public class DbManagementControllerTests
{
    private readonly Mock<IDbManagementService> _dbManagementService = new();
    private readonly Mock<IAuditService> _auditService = new();

    private DbManagementController CreateController()
        => new(_dbManagementService.Object, _auditService.Object);

    [Fact]
    public async Task CreateDb_ShouldRejectBlankPassword_ForCredentialBasedProvider()
    {
        var request = new DbManagementRequest
        {
            Name = "Production",
            SqlProvider = "Postgres",
            Password = ""
        };

        var result = await CreateController().CreateDb(
            request,
            TestContext.Current.CancellationToken);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Password is required for this SQL provider.", badRequest.Value);
        _dbManagementService.Verify(
            s => s.CreateDbAsync(It.IsAny<DbManagementRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateDb_ShouldAllowBlankPassword_ForSqlite()
    {
        var request = new DbManagementRequest
        {
            Name = "Local",
            SqlProvider = "Sqlite",
            Database = "local.db"
        };
        _dbManagementService
            .Setup(s => s.CreateDbAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DbManagementVM
            {
                Id = 7,
                Name = request.Name,
                SqlProvider = request.SqlProvider,
                Database = request.Database
            });

        var result = await CreateController().CreateDb(
            request,
            TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result);
        _dbManagementService.Verify(
            s => s.CreateDbAsync(request, It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
