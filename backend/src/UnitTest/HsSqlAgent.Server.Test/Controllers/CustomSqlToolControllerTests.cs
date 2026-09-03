using System.Data.Common;
using System.Text.Json;
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
using SqlAgent.Service.Factories;
using Xunit;

namespace HsSqlAgent.Server.Test.Controllers;

public class CustomSqlToolControllerTests
{
    private readonly Mock<ICustomSqlToolService> _toolServiceMock = new();
    private readonly Mock<IAuditService> _auditServiceMock = new();

    private CustomSqlToolController CreateController()
    {
        var controller = new CustomSqlToolController(_toolServiceMock.Object, _auditServiceMock.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
                {
                    User = new System.Security.Claims.ClaimsPrincipal(
                        new System.Security.Claims.ClaimsIdentity())
                }
            }
        };
        return controller;
    }

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
    public async Task Publish_InsertValues_ShouldPassDefinitionValidation()
    {
        var controller = CreateController();
        var tool = new CustomSqlTool
        {
            Id = 12,
            Name = "insert_user",
            Description = "Insert one user",
            Type = "DML",
            SqlTemplate = "INSERT INTO public.users (id) VALUES ({{id}})",
            ParametersJson = """[{"name":"id","type":"number"}]""",
            DbManagementId = 3
        };
        _toolServiceMock.Setup(s => s.GetToolByIdAsync(tool.Id)).ReturnsAsync(tool);
        _toolServiceMock.Setup(s => s.PublishAsync(tool.Id, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(tool);
        var policy = new Mock<ISecurityPolicyRuntimeState>();
        policy.Setup(x => x.GetCurrent()).Returns(new SecurityPolicyModel());
        var dbService = CreateDbService(3);

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
    public async Task Publish_InsertSelect_ShouldRemainFailClosed()
    {
        var controller = CreateController();
        var tool = new CustomSqlTool
        {
            Id = 13,
            Name = "copy_users",
            Description = "Copy users",
            Type = "DML",
            SqlTemplate = "INSERT INTO public.users (id) SELECT id FROM public.pending_users",
            DbManagementId = 3
        };
        _toolServiceMock.Setup(s => s.GetToolByIdAsync(tool.Id)).ReturnsAsync(tool);
        var policy = new Mock<ISecurityPolicyRuntimeState>();
        policy.Setup(x => x.GetCurrent()).Returns(new SecurityPolicyModel());
        var dbService = CreateDbService(3);

        var result = await controller.Publish(
            tool.Id,
            policy.Object,
            dbService.Object,
            TestContext.Current.CancellationToken);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var json = JsonSerializer.Serialize(badRequest.Value);
        Assert.Contains("INSERT ... SELECT", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fail-closed", json, StringComparison.OrdinalIgnoreCase);
        _toolServiceMock.Verify(
            s => s.PublishAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TestExecute_Query_UsesTypedRuntime()
    {
        var controller = CreateController();
        var tool = new CustomSqlTool
        {
            Id = 10,
            Name = "find_user",
            Description = "Find one user",
            Type = "Query",
            SqlTemplate = "SELECT id FROM users WHERE id = {{id}}",
            ParametersJson = """[{"name":"id","type":"number"}]""",
            DbManagementId = 3
        };
        _toolServiceMock.Setup(s => s.GetToolByIdAsync(tool.Id)).ReturnsAsync(tool);
        var dbService = CreateDbService(3);
        var crypto = CreateCrypto();
        var providerFactory = new Mock<ISqlProviderFactory>();
        var connectionStringFactory = new Mock<ISqlConnectionStringFactory>();
        ConfigureProviderFactories(providerFactory, connectionStringFactory);
        var runtimePolicy = new SecurityPolicyModel { QueryMaxRows = 50, QueryTimeoutSeconds = 15 };
        var policy = new Mock<ISecurityPolicyRuntimeState>();
        policy.Setup(x => x.GetCurrent()).Returns(runtimePolicy);
        var limiter = CreateLimiter();
        var typedQueryRuntime = new Mock<ITypedQueryRuntime>();
        typedQueryRuntime.Setup(x => x.ExecuteAsync(
                It.Is<ISqlProvider>(provider => provider.Type == SqlAgentToolType.Postgres),
                "connection",
                It.Is<string>(sql => sql.Contains("users", StringComparison.OrdinalIgnoreCase)),
                SqlAgentToolType.Postgres,
                runtimePolicy,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryExecutionResult(
                [new Dictionary<string, object?> { ["id"] = 1 }],
                1,
                TimeSpan.Zero,
                []));

        var result = await controller.TestExecute(
            new CustomToolTestExecuteRequest(tool.Id, new Dictionary<string, object?> { ["id"] = 1 }),
            providerFactory.Object,
            connectionStringFactory.Object,
            dbService.Object,
            crypto.Object,
            Options.Create(new McpKeySettings { HmacSecretKey = new string('x', 32) }),
            policy.Object,
            limiter.Object,
            typedQueryRuntime.Object,
            TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result);
        typedQueryRuntime.VerifyAll();
    }

    [Fact]
    public async Task TestExecute_InsertValues_ReturnsTypedPreviewWithoutCommit()
    {
        var controller = CreateController();
        controller.ControllerContext.HttpContext.User =
            new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    [new System.Security.Claims.Claim("sub", "admin-test")],
                    "test"));
        var tool = new CustomSqlTool
        {
            Id = 14,
            Name = "insert_user",
            Description = "Insert one user",
            Type = "DML",
            SqlTemplate = "INSERT INTO public.users (id) VALUES ({{id}})",
            ParametersJson = """[{"name":"id","type":"number"}]""",
            DbManagementId = 3
        };
        _toolServiceMock.Setup(s => s.GetToolByIdAsync(tool.Id)).ReturnsAsync(tool);
        var dbService = CreateDbService(3);
        var crypto = CreateCrypto();
        var providerFactory = new Mock<ISqlProviderFactory>();
        var connectionStringFactory = new Mock<ISqlConnectionStringFactory>();
        var metadata = new Mock<IProviderMetadataReader>(MockBehavior.Strict);
        var verificationConnection = new Mock<DbConnection>();
        verificationConnection.SetupGet(x => x.State).Returns(System.Data.ConnectionState.Open);
        verificationConnection.SetupGet(x => x.ServerVersion).Returns("17.5");
        verificationConnection.Setup(x => x.OpenAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var connections = new Mock<IDbConnectionFactory>(MockBehavior.Strict);
        connections.Setup(x => x.Create("connection")).Returns(verificationConnection.Object);
        var provider = new Mock<ISqlProvider>();
        provider.SetupGet(x => x.Type).Returns(SqlAgentToolType.Postgres);
        provider.SetupGet(x => x.Metadata).Returns(metadata.Object);
        provider.SetupGet(x => x.Connections).Returns(connections.Object);
        providerFactory.Setup(x => x.GetProvider(SqlAgentToolType.Postgres)).Returns(provider.Object);
        connectionStringFactory.Setup(x => x.BuildConnectionString(
                SqlAgentToolType.Postgres,
                It.IsAny<BuildDbConnectionModelBase>()))
            .Returns("connection");
        var policy = new Mock<ISecurityPolicyRuntimeState>();
        policy.Setup(x => x.GetCurrent()).Returns(new SecurityPolicyModel { DmlMaxAffectedRows = 1 });
        var limiter = CreateLimiter();
        var typedQueryRuntime = new Mock<ITypedQueryRuntime>(MockBehavior.Strict);

        var result = await controller.TestExecute(
            new CustomToolTestExecuteRequest(tool.Id, new Dictionary<string, object?> { ["id"] = 1 }),
            providerFactory.Object,
            connectionStringFactory.Object,
            dbService.Object,
            crypto.Object,
            Options.Create(new McpKeySettings { HmacSecretKey = new string('x', 32) }),
            policy.Object,
            limiter.Object,
            typedQueryRuntime.Object,
            TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result);
        using var outer = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        Assert.True(outer.RootElement.GetProperty("success").GetBoolean());
        var data = outer.RootElement.GetProperty("data").GetString();
        Assert.NotNull(data);
        using var preview = JsonDocument.Parse(data!);
        Assert.Equal("Insert", preview.RootElement.GetProperty("operation").GetString());
        Assert.Equal(1, preview.RootElement.GetProperty("affectedRows").GetInt32());
        Assert.False(preview.RootElement.GetProperty("committed").GetBoolean());
        connections.Verify(x => x.Create("connection"), Times.Once);
        metadata.VerifyNoOtherCalls();
        typedQueryRuntime.VerifyNoOtherCalls();
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


    private static Mock<IDbManagementService> CreateDbService(int id)
    {
        var dbService = new Mock<IDbManagementService>();
        dbService.Setup(x => x.GetDbByIdAsync(id, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DbManagementVM { Id = id, SqlProvider = "Postgres" });
        dbService.Setup(x => x.GetDbByIdAsync(id, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DbManagementPwdVM
            {
                Id = id,
                SqlProvider = "Postgres",
                Host = "localhost",
                Database = "test",
                Username = "user",
                PasswordHash = "encrypted"
            });
        return dbService;
    }

    private static Mock<ICryptoService> CreateCrypto()
    {
        var crypto = new Mock<ICryptoService>();
        crypto.Setup(x => x.DecryptText("encrypted", It.IsAny<byte[]>())).Returns("password");
        return crypto;
    }

    private static void ConfigureProviderFactories(
        Mock<ISqlProviderFactory> providerFactory,
        Mock<ISqlConnectionStringFactory> connectionStringFactory)
    {
        var provider = new Mock<ISqlProvider>();
        provider.SetupGet(x => x.Type).Returns(SqlAgentToolType.Postgres);
        providerFactory.Setup(x => x.GetProvider(SqlAgentToolType.Postgres)).Returns(provider.Object);
        connectionStringFactory.Setup(x => x.BuildConnectionString(
                SqlAgentToolType.Postgres,
                It.IsAny<BuildDbConnectionModelBase>()))
            .Returns("connection");
    }

    private static Mock<ISqlExecutionConcurrencyLimiter> CreateLimiter()
    {
        var limiter = new Mock<ISqlExecutionConcurrencyLimiter>();
        limiter.Setup(x => x.TryAcquireAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IAsyncDisposable>());
        return limiter;
    }
}
