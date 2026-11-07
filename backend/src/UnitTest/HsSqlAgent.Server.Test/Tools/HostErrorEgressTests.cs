using System.Text.Json;
using HsSqlAgent.Server.Middleware;
using HsSqlAgent.Server.Models;
using HsSqlAgent.Server.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

public sealed class HostErrorEgressTests
{
    private const string Secret = "Password=super-secret; SQL=DROP TABLE private.payments";

    [Theory]
    [InlineData("Postgres")]
    [InlineData("Sqlite")]
    [InlineData("MsSqlServer")]
    [InlineData("Firebird")]
    [InlineData("MySQL")]
    [InlineData("Oracle")]
    public void EveryDdlProvider_FailedOrRejectedRuntimeMessage_IsRedacted(string provider)
    {
        foreach (var status in new[] { DdlRuntimeStatus.Failed, DdlRuntimeStatus.Rejected,
                     DdlRuntimeStatus.OutcomeUnknown })
        {
            const string driverCode = "provider exception: Password=super-secret";
            var result = provider switch
            {
                "Postgres" => McpDdlToolResult.FromRuntime(new DdlRuntimeResult(
                    status, null, Secret, driverCode)),
                "Sqlite" => McpDdlToolResult.FromSqliteRuntime(new SqliteDdlRuntimeResult(
                    status, null, Secret, driverCode)),
                "MsSqlServer" => McpDdlToolResult.FromSqlServerRuntime(new SqlServerDdlRuntimeResult(
                    status, null, Secret, driverCode)),
                "Firebird" => McpDdlToolResult.FromFirebirdRuntime(new FirebirdDdlRuntimeResult(
                    status, null, Secret, driverCode)),
                "MySQL" => McpDdlToolResult.FromMySqlRuntime(new MySqlDdlRuntimeResult(
                    status, null, Secret, driverCode)),
                "Oracle" => McpDdlToolResult.FromOracleRuntime(new OracleDdlRuntimeResult(
                    status, null, Secret, driverCode)),
                _ => throw new InvalidOperationException("Unknown test provider.")
            };

            Assert.Equal(provider, result.Provider);
            Assert.False(result.CommitConfirmed);
            Assert.NotNull(result.Error);
            Assert.False(result.Error!.Retryable);
            if (status == DdlRuntimeStatus.OutcomeUnknown)
            {
                Assert.Equal("outcome_unknown", result.Status);
                Assert.Equal("ddl.outcome_unknown", result.Error.Code);
            }
            Assert.DoesNotContain("super-secret", JsonSerializer.Serialize(result));
            Assert.DoesNotContain("payments", JsonSerializer.Serialize(result));
            Assert.DoesNotContain("provider exception", JsonSerializer.Serialize(result));
        }
    }

    [Theory]
    [InlineData("argument", 400)]
    [InlineData("authorization", 401)]
    [InlineData("missing", 404)]
    [InlineData("conflict", 409)]
    [InlineData("unexpected", 500)]
    public async Task HttpUnhandledException_NeverReflectsDatabaseCredentialsOrSql(string category, int expectedStatus)
    {
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(x => x.EnvironmentName).Returns("Production");
        using var services = new ServiceCollection().AddSingleton(environment.Object).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        using var body = new MemoryStream();
        context.Response.Body = body;
        Exception ex = category switch
        {
            "argument" => new ArgumentException(Secret),
            "authorization" => new UnauthorizedAccessException(Secret),
            "missing" => new KeyNotFoundException(Secret),
            "conflict" => new InvalidOperationException(Secret),
            _ => new IOException(Secret)
        };

        var handler = new GlobalExceptionHandler(Mock.Of<ILogger<GlobalExceptionHandler>>());
        Assert.True(await handler.TryHandleAsync(context, ex, TestContext.Current.CancellationToken));
        Assert.Equal(expectedStatus, context.Response.StatusCode);
        body.Position = 0;
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: TestContext.Current.CancellationToken);
        var response = json.RootElement;
        Assert.False(string.IsNullOrWhiteSpace(response.GetProperty("error").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(response.GetProperty("traceId").GetString()));
        Assert.False(response.TryGetProperty("code", out _));
        Assert.DoesNotContain("super-secret", response.ToString());
        Assert.DoesNotContain("payments", response.ToString());
    }

    [Fact]
    public async Task HttpUnknownOutcome_MustNotReflectInnerProviderFailure()
    {
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(x => x.EnvironmentName).Returns("Production");
        using var services = new ServiceCollection().AddSingleton(environment.Object).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        using var body = new MemoryStream();
        context.Response.Body = body;
        var ex = new SqlAgent.Service.Core.Execution.DmlCommitOutcomeUnknownException(
            new IOException(Secret));
        var handler = new GlobalExceptionHandler(Mock.Of<ILogger<GlobalExceptionHandler>>());

        Assert.True(await handler.TryHandleAsync(context, ex, TestContext.Current.CancellationToken));
        body.Position = 0;
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: TestContext.Current.CancellationToken);
        var response = json.RootElement;
        Assert.Equal("dml.commit_outcome_unknown", response.GetProperty("code").GetString());
        Assert.False(response.GetProperty("retryable").GetBoolean());
        Assert.DoesNotContain("super-secret", response.ToString());
    }
}
