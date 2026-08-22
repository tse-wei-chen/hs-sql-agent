using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Common.Interfaces;
using HsSqlAgent.Server.Controllers;
using HsSqlAgent.Server.Models;
using HsSqlAgent.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Moq;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Factories;
using SqlAgent.Service.Models;
using SqlAgent.Service.Strategies;
using Xunit;

namespace HsSqlAgent.Server.Test.Controllers;

public class CustomSqlToolControllerTests
{
    private readonly Mock<ICustomSqlToolService> _toolServiceMock = new();
    private readonly Mock<IAuditService> _auditServiceMock = new();

    private CustomSqlToolController CreateController()
        => new(_toolServiceMock.Object, _auditServiceMock.Object);

    [Fact]
    public async Task Publish_ShouldRejectSqlThatRuntimeParserCannotAccept()
    {
        var controller = CreateController();
        var tool = new CustomSqlTool
        {
            Id = 9,
            Name = "broken_query",
            Description = "Draft",
            Type = "Query",
            SqlTemplate = "not valid sql",
            DbManagementId = 1
        };
        _toolServiceMock.Setup(s => s.GetToolByIdAsync(tool.Id)).ReturnsAsync(tool);
        var policy = new Mock<ISecurityPolicyRuntimeState>();
        policy.Setup(x => x.GetCurrent()).Returns(new SecurityPolicyModel());
        var dbService = new Mock<IDbManagementService>();
        dbService.Setup(x => x.GetDbByIdAsync(1, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DbManagementVM { Id = 1, SqlProvider = "Postgres" });

        var result = await controller.Publish(
            tool.Id,
            policy.Object,
            dbService.Object,
            TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result);
        _toolServiceMock.Verify(
            s => s.PublishAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Publish_ShouldUseBoundDatabaseDialect()
    {
        var controller = CreateController();
        var tool = new CustomSqlTool
        {
            Id = 11,
            Name = "provider_query",
            Description = "Provider aware",
            Type = "Query",
            SqlTemplate = "SELECT TOP 1 id FROM users",
            DbManagementId = 2
        };
        _toolServiceMock.Setup(s => s.GetToolByIdAsync(tool.Id)).ReturnsAsync(tool);
        _toolServiceMock.Setup(s => s.PublishAsync(tool.Id, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(tool);
        var policy = new Mock<ISecurityPolicyRuntimeState>();
        policy.Setup(x => x.GetCurrent()).Returns(new SecurityPolicyModel());
        var dbService = new Mock<IDbManagementService>();
        dbService.Setup(x => x.GetDbByIdAsync(2, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DbManagementVM { Id = 2, SqlProvider = "MsSqlServer" });

        var result = await controller.Publish(
            tool.Id,
            policy.Object,
            dbService.Object,
            TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result);
        _toolServiceMock.Verify(
            s => s.PublishAsync(tool.Id, It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task TestExecute_Dml_ShouldOnlyPerformRollbackDryRun()
    {
        var controller = CreateController();
        var tool = new CustomSqlTool
        {
            Id = 10,
            Name = "delete_user",
            Description = "Delete one user",
            Type = "DML",
            SqlTemplate = "DELETE FROM users WHERE id = {{id}}",
            ParametersJson = """[{"name":"id","type":"number"}]""",
            DbManagementId = 3
        };
        _toolServiceMock.Setup(s => s.GetToolByIdAsync(tool.Id)).ReturnsAsync(tool);
        var dbService = new Mock<IDbManagementService>();
        dbService.Setup(x => x.GetDbByIdAsync(3, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DbManagementVM { Id = 3, SqlProvider = "Postgres" });
        dbService.Setup(x => x.GetDbByIdAsync(3, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DbManagementPwdVM
            {
                Id = 3,
                SqlProvider = "Postgres",
                Host = "localhost",
                Database = "test",
                Username = "user",
                PasswordHash = "encrypted"
            });
        var crypto = new Mock<ICryptoService>();
        crypto.Setup(x => x.DecryptText("encrypted", It.IsAny<byte[]>())).Returns("password");
        var strategy = new Mock<ISqlStrategy>();
        strategy.Setup(x => x.BuildConnectionString(It.IsAny<BuildDbConnectionModelBase>())).Returns("connection");
        var observedTokens = new List<string?>();
        strategy.Setup(x => x.ExecuteDmlAsync(
                "connection",
                It.IsAny<DmlDefinition>(),
                It.IsAny<SqlExecutionPolicy>(),
                It.IsAny<CancellationToken>()))
            .Callback<string?, DmlDefinition?, SqlExecutionPolicy?, CancellationToken>((_, dml, _, _) => observedTokens.Add(dml?.ConfirmToken))
            .ReturnsAsync("Dry Run Result | affectedRows=1 | TokenRequired=secret | Security Note: not committed.");
        var strategyFactory = new Mock<ISqlStrategyFactory>();
        strategyFactory.Setup(x => x.GetStrategy(SqlAgentToolType.Postgres)).Returns(strategy.Object);
        var policy = new Mock<ISecurityPolicyRuntimeState>();
        policy.Setup(x => x.GetCurrent()).Returns(new SecurityPolicyModel
        {
            AllowFullTableDelete = false,
            RequireWhereForDelete = true,
            DmlMaxAffectedRows = 100
        });
        var limiter = new Mock<ISqlExecutionConcurrencyLimiter>();
        limiter.Setup(x => x.TryAcquireAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Mock.Of<IAsyncDisposable>());

        var result = await controller.TestExecute(
            new CustomToolTestExecuteRequest(tool.Id, new Dictionary<string, object?> { ["id"] = 1 }),
            strategyFactory.Object,
            dbService.Object,
            crypto.Object,
            Options.Create(new McpKeySettings { HmacSecretKey = new string('x', 32) }),
            policy.Object,
            limiter.Object,
            TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal([null], observedTokens);
        strategy.Verify(x => x.ExecuteDmlAsync(
            It.IsAny<string>(),
            It.IsAny<DmlDefinition>(),
            It.IsAny<SqlExecutionPolicy>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateTool_ShouldAllowIncompleteSqlAsDraft()
    {
        var controller = CreateController();
        var tool = new CustomSqlTool
        {
            Name = "bad_query",
            Description = "Bad query",
            Type = "Query",
            SqlTemplate = "not valid sql",
            DbManagementId = 1
        };
        _toolServiceMock.Setup(s => s.CreateToolAsync(tool)).ReturnsAsync(tool);

        var result = await controller.CreateTool(tool);

        Assert.IsType<CreatedAtActionResult>(result);
        _toolServiceMock.Verify(s => s.CreateToolAsync(tool), Times.Once);
    }

    [Fact]
    public async Task UpdateTool_ShouldAllowIncompleteDmlAsDraft()
    {
        var controller = CreateController();
        var tool = new CustomSqlTool
        {
            Id = 7,
            Name = "bad_dml",
            Description = "Bad DML",
            Type = "DML",
            SqlTemplate = "DELETE",
            DbManagementId = 1
        };
        _toolServiceMock.Setup(s => s.GetToolByIdAsync(7)).ReturnsAsync(tool);
        _toolServiceMock.Setup(s => s.UpdateToolAsync(tool)).ReturnsAsync(tool);

        var result = await controller.UpdateTool(tool.Id, tool);

        Assert.IsType<OkObjectResult>(result);
        _toolServiceMock.Verify(s => s.UpdateToolAsync(tool), Times.Once);
    }

    [Fact]
    public async Task CreateTool_ShouldSaveValidQueryDefinition()
    {
        var controller = CreateController();
        var tool = new CustomSqlTool
        {
            Name = "good_query",
            Description = "Good query",
            Type = "Query",
            SqlTemplate = "SELECT * FROM users",
            DbManagementId = 1
        };
        _toolServiceMock
            .Setup(s => s.CreateToolAsync(tool))
            .ReturnsAsync(tool);

        var result = await controller.CreateTool(tool);

        Assert.IsType<CreatedAtActionResult>(result);
        _toolServiceMock.Verify(s => s.CreateToolAsync(tool), Times.Once);
    }
}
