using System.Data.Common;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using HsSqlAgent.Server.Services;
using Moq;
using SqlAgent.Service.Interfaces;
using Xunit;

namespace HsSqlAgent.Server.Test.Services.Dml;

/// <summary>
/// Preflight runtime-profile discovery is physical database I/O. It must not
/// open a connection without owning the same SQL budget as preview and commit.
/// </summary>
public sealed class DmlPreflightConcurrencyBoundaryTests
{
    [Fact]
    public async Task Saturation_DeniesBeforeCreatingPhysicalConnection()
    {
        var state = new Mock<ISecurityPolicyRuntimeState>();
        state.Setup(x => x.GetCurrent()).Returns(new SecurityPolicyModel
        {
            MaxConcurrentSql = 1
        });
        var limiter = new SqlExecutionConcurrencyLimiter(state.Object);
        await using var held = await limiter.TryAcquireAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(held);

        var factory = new Mock<IDbConnectionFactory>(MockBehavior.Strict);
        var provider = new Mock<ISqlProvider>(MockBehavior.Strict);
        provider.SetupGet(x => x.Type).Returns(SqlAgentToolType.Postgres);
        provider.SetupGet(x => x.Connections).Returns(factory.Object);
        var runtime = new TypedDmlRuntime();

        var denied = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.ParseDmlBatchWithVerifiedRuntimeProfileUnderLeaseAsync(
                provider.Object, "Host=unused;Database=unused",
                "INSERT INTO public.users (id) VALUES (7)",
                SqlAgentToolType.Postgres, limiter, TestContext.Current.CancellationToken));

        Assert.Contains("Server busy:", denied.Message, StringComparison.Ordinal);
        factory.VerifyNoOtherCalls();
        Assert.Equal(1, limiter.ActiveCount);
    }

    [Fact]
    public async Task CancellationDuringConnectionOpen_ReleasesBudgetImmediately()
    {
        var state = new Mock<ISecurityPolicyRuntimeState>();
        state.Setup(x => x.GetCurrent()).Returns(new SecurityPolicyModel
        {
            MaxConcurrentSql = 1
        });
        var limiter = new SqlExecutionConcurrencyLimiter(state.Object);
        using var cancellation = new CancellationTokenSource();
        var connection = new Mock<DbConnection>();
        connection.Setup(x => x.OpenAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(_ =>
            {
                cancellation.Cancel();
                return Task.FromCanceled(cancellation.Token);
            });
        var factory = new Mock<IDbConnectionFactory>(MockBehavior.Strict);
        factory.Setup(x => x.Create("Host=unused;Database=unused"))
            .Returns(connection.Object);
        var provider = new Mock<ISqlProvider>(MockBehavior.Strict);
        provider.SetupGet(x => x.Type).Returns(SqlAgentToolType.Postgres);
        provider.SetupGet(x => x.Connections).Returns(factory.Object);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new TypedDmlRuntime().ParseDmlBatchWithVerifiedRuntimeProfileUnderLeaseAsync(
                provider.Object, "Host=unused;Database=unused",
                "INSERT INTO public.users (id) VALUES (7)",
                SqlAgentToolType.Postgres, limiter, cancellation.Token));

        Assert.Equal(0, limiter.ActiveCount);
        await using var next = await limiter.TryAcquireAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(next);
        Assert.Equal(1, limiter.ActiveCount);
        factory.Verify(x => x.Create("Host=unused;Database=unused"), Times.Once);
    }
}
