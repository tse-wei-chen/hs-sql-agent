using System.Reflection;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Common.Interfaces;
using HsSqlAgent.Server.Controllers;
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
    public void TestDbConnectionRequest_ExistingModeFields_AreNullableInClrMetadata()
    {
        var context = new NullabilityInfoContext();
        var names = new[]
        {
            nameof(TestDbConnectionRequest.Host),
            nameof(TestDbConnectionRequest.Port),
            nameof(TestDbConnectionRequest.Username),
            nameof(TestDbConnectionRequest.Password),
            nameof(TestDbConnectionRequest.Database),
            nameof(TestDbConnectionRequest.ExtraSettings)
        };

        foreach (var name in names)
        {
            var property = Assert.IsType<PropertyInfo>(
                typeof(TestDbConnectionRequest).GetProperty(name));

            var nullability = context.Create(property);
            Assert.Equal(NullabilityState.Nullable, nullability.ReadState);
            Assert.Equal(NullabilityState.Nullable, nullability.WriteState);
        }
    }

    [Fact]
    public async Task TestDbConnection_ExistingMode_LoadsSavedConnectionFromDbManagement()
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

        dbManagementService
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

        cryptoService
            .Setup(service => service.DecryptText(
                "ciphertext",
                It.IsAny<byte[]>()))
            .Returns("secret");

        TestDbConnectionBase? captured = null;
        tester
            .Setup(service => service.TestDbConnectionAsync(
                It.IsAny<TestDbConnectionBase>(),
                It.IsAny<CancellationToken>()))
            .Callback<TestDbConnectionBase, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new TestDbConnectionVM { IsSuccess = true });

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

        var result = await controller.TestDbConnection(
            new TestDbConnectionRequest
            {
                DbSettingMode = 0,
                DbManagementId = 1
            },
            TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result);
        var request = Assert.IsType<TestDbConnectionRequest>(captured);
        Assert.Equal(SqlAgentToolType.Postgres, request.SqlProvider);
        Assert.Equal("db.internal", request.Host);
        Assert.Equal("5432", request.Port);
        Assert.Equal("agent", request.Username);
        Assert.Equal("secret", request.Password);
        Assert.Equal("app", request.Database);
        Assert.Null(request.ExtraSettings);

        dbManagementService.Verify(service => service.GetDbByIdAsync(
            1,
            true,
            It.IsAny<CancellationToken>()), Times.Once);
        tester.Verify(service => service.TestDbConnectionAsync(
            It.IsAny<TestDbConnectionBase>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
