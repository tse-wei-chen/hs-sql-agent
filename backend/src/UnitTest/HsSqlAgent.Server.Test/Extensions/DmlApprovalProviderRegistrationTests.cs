using HsSqlAgent.Approvals;
using HsSqlAgent.Server.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HsSqlAgent.Server.Test.Extensions;

public sealed class DmlApprovalProviderRegistrationTests
{
    [Fact]
    public void AddHsSqlAgentDmlApproval_RegistersHostProvider()
    {
        var services = new ServiceCollection();
        var hs = services.AddHsSqlAgentCore();

        hs.AddHsSqlAgentDmlApproval<TestApprovalProvider>();

        using var provider = services.BuildServiceProvider();
        Assert.IsType<TestApprovalProvider>(provider.GetRequiredService<IDmlApprovalProvider>());
    }

    [Fact]
    public void AddHsSqlAgentRuntime_RegistersCompletionSinkWithoutImplicitAdminStore()
    {
        var services = new ServiceCollection();
        var hs = services.AddHsSqlAgentCore();

        hs.AddHsSqlAgentRuntime();

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IDmlApprovalCompletionSink));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(Admin.Service.Data.AdminContext));
    }

    [Fact]
    public void AddHsSqlAgentDmlApproval_RejectsAmbiguousSecondProvider()
    {
        var services = new ServiceCollection();
        var hs = services.AddHsSqlAgentCore();
        hs.AddHsSqlAgentDmlApproval<TestApprovalProvider>();

        Assert.Throws<InvalidOperationException>(() =>
            hs.AddHsSqlAgentDmlApproval<OtherApprovalProvider>());
    }

    [Fact]
    public void ApprovalAbstractions_DoNotReferenceExecutionAssemblies()
    {
        var references = typeof(IDmlApprovalProvider).Assembly
            .GetReferencedAssemblies()
            .Select(x => x.Name)
            .ToArray();

        Assert.DoesNotContain("HsSqlAgent.Server", references);
        Assert.DoesNotContain("SqlAgent.Service", references);
        Assert.DoesNotContain("HsSqlAgent.SqlCore", references);
    }

    private sealed class TestApprovalProvider : IDmlApprovalProvider
    {
        public ValueTask<DmlApprovalResult> RequestApprovalAsync(
            DmlApprovalRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DmlApprovalResult.Reject(request));
    }

    private sealed class OtherApprovalProvider : IDmlApprovalProvider
    {
        public ValueTask<DmlApprovalResult> RequestApprovalAsync(
            DmlApprovalRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DmlApprovalResult.Reject(request));
    }
}
