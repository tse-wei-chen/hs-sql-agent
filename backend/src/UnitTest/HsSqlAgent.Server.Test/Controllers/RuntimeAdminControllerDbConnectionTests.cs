using System.Reflection;
using System.Text.Json;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Common.Interfaces;
using HsSqlAgent.Server.Controllers;
using HsSqlAgent.Server.Models;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Moq;
using SqlAgent.Service.Interfaces;
using Xunit;

namespace HsSqlAgent.Server.Test.Controllers;

public sealed class RuntimeAdminControllerDbConnectionTests
{
    [Fact]
    public void TestDbConnectionHttpRequest_ExistingModeFields_AreNullableInClrMetadata()
    {
        var context = new NullabilityInfoContext();
        var names = new[]
        {
            nameof(TestDbConnectionHttpRequest.Host),
            nameof(TestDbConnectionHttpRequest.Port),
            nameof(TestDbConnectionHttpRequest.Username),
            nameof(TestDbConnectionHttpRequest.Password),
            nameof(TestDbConnectionHttpRequest.Database),
            nameof(TestDbConnectionHttpRequest.ExtraSettings)
        };

        foreach (var name in names)
        {
            var property = Assert.IsAssignableFrom<PropertyInfo>(
                typeof(TestDbConnectionHttpRequest).GetProperty(name));

            var nullability = context.Create(property);
            Assert.Equal(NullabilityState.Nullable, nullability.ReadState);
            Assert.Equal(NullabilityState.Nullable, nullability.WriteState);
        }
    }

    [Theory]
    [InlineData("\"Postgres\"", SqlAgentToolType.Postgres)]
    [InlineData("\"postgres\"", SqlAgentToolType.Postgres)]
    [InlineData("1", SqlAgentToolType.Postgres)]
    public void TestDbConnectionHttpRequest_SqlProvider_AcceptsLegacyStringAndNumericWireValues(
        string providerJson,
        SqlAgentToolType expected)
    {
        var request = JsonSerializer.Deserialize<TestDbConnectionHttpRequest>(
            $"{{\"dbSettingMode\":1,\"sqlProvider\":{providerJson}}}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(request);
        Assert.Equal(expected, request.SqlProvider);
    }

    [Fact]
    public async Task TestDbConnection_NewMode_MapsWireRequestToTypedServiceRequest()
    {
        var controllerContext = CreateControllerContext();
        TestDbConnectionBase? captured = null;
        controllerContext.Tester
            .Setup(service => service.TestDbConnectionAsync(
                It.IsAny<TestDbConnectionBase>(),
                It.IsAny<CancellationToken>()))
            .Callback<TestDbConnectionBase, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new TestDbConnectionVM { IsSuccess = true });

        var result = await controllerContext.Controller.TestDbConnection(
            new TestDbConnectionHttpRequest
            {
                DbSettingMode = 1,
                SqlProvider = SqlAgentToolType.Postgres,
                Host = "db.internal",
                Port = "5432",
                Username = "agent",
                Password = "secret",
                Database = "app",
                ExtraSettings = "{}"
            },
            TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result);
        var request = Assert.IsType<TestDbConnectionBase>(captured);
        Assert.Equal(SqlAgentToolType.Postgres, request.SqlProvider);
        Assert.Equal("db.internal", request.Host);
        Assert.Equal("5432", request.Port);
        Assert.Equal("agent", request.Username);
        Assert.Equal("secret", request.Password);
        Assert.Equal("app", request.Database);
        Assert.Equal("{}", request.ExtraSettings);
    }

    [Fact]
    public async Task TestDbConnection_ExistingMode_LoadsSavedConnectionFromDbManagement()
    {
        var controllerContext = CreateControllerContext();

        controllerContext.DbManagementService
            .Setup(service => service.GetDbByIdAsync(
                1,
                true,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DbManagementPwdVM
            {
                Id = 1,
                Name = "primary",
                SqlProvider = "Postgres",
                Host = "db.internal",
                Port = "5432",
                Username = "agent",
                PasswordHash = "ciphertext",
                Database = "app",
                ExtraSettings = null
            });

        controllerContext.CryptoService
            .Setup(service => service.DecryptText(
                "ciphertext",
                It.IsAny<byte[]>()))
            .Returns("secret");

        TestDbConnectionBase? captured = null;
        controllerContext.Tester
            .Setup(service => service.TestDbConnectionAsync(
                It.IsAny<TestDbConnectionBase>(),
                It.IsAny<CancellationToken>()))
            .Callback<TestDbConnectionBase, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new TestDbConnectionVM { IsSuccess = true });

        var result = await controllerContext.Controller.TestDbConnection(
            new TestDbConnectionHttpRequest
            {
                DbSettingMode = 0,
                DbManagementId = 1
            },
            TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result);
        var request = Assert.IsType<TestDbConnectionBase>(captured);
        Assert.Equal(SqlAgentToolType.Postgres, request.SqlProvider);
        Assert.Equal("db.internal", request.Host);
        Assert.Equal("5432", request.Port);
        Assert.Equal("agent", request.Username);
        Assert.Equal("secret", request.Password);
        Assert.Equal("app", request.Database);
        Assert.Null(request.ExtraSettings);

        controllerContext.DbManagementService.Verify(service => service.GetDbByIdAsync(
            1,
            true,
            It.IsAny<CancellationToken>()), Times.Once);
        controllerContext.Tester.Verify(service => service.TestDbConnectionAsync(
            It.IsAny<TestDbConnectionBase>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static ControllerContextFixture CreateControllerContext()
    {
        var keyService = new Mock<IMcpAccessKeyService>();
        var tester = new Mock<IDbSetterService>();
        var auditService = new Mock<IAuditService>();
        var dbManagementService = new Mock<IDbManagementService>();
        var cryptoService = new Mock<ICryptoService>();
        var operabilityService = new Mock<IOperabilityService>();
        var auditRetentionService = new Mock<IAuditRetentionService>();
        var customSqlToolService = new Mock<ICustomSqlToolService>();
        var settings = Options.Create(new McpKeySettings
        {
            HmacSecretKey = new string('x', 32)
        });

        var controller = new RuntimeAdminController(
            keyService.Object,
            tester.Object,
            auditService.Object,
            dbManagementService.Object,
            cryptoService.Object,
            settings,
            operabilityService.Object,
            auditRetentionService.Object,
            customSqlToolService.Object);

        return new ControllerContextFixture(
            controller,
            tester,
            dbManagementService,
            cryptoService);
    }

    private sealed record ControllerContextFixture(
        RuntimeAdminController Controller,
        Mock<IDbSetterService> Tester,
        Mock<IDbManagementService> DbManagementService,
        Mock<ICryptoService> CryptoService);
}
