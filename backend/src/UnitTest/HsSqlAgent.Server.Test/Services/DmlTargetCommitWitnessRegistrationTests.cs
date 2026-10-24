using HsSqlAgent.Server.Extensions;
using HsSqlAgent.Server.Models;
using HsSqlAgent.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public sealed class DmlTargetCommitWitnessRegistrationTests
{
    [Fact]
    public void WitnessIsOptIn_AndRegisteredWithRuntime()
    {
        var defaultServices = new ServiceCollection();
        defaultServices.AddHsSqlAgentCore(V3TestEncryption.Prepare(new HsSqlAgentServiceOptions())).AddHsSqlAgentRuntime();
        Assert.DoesNotContain(defaultServices,
            descriptor => descriptor.ServiceType == typeof(IDmlTargetCommitWitness));

        var enabled = new ServiceCollection();
        var builder = enabled.AddHsSqlAgentCore(V3TestEncryption.Prepare(new HsSqlAgentServiceOptions())).AddHsSqlAgentTargetCommitWitness();
        Assert.Contains(enabled, descriptor =>
            descriptor.ServiceType == typeof(IDmlTargetCommitWitness)
            && descriptor.ImplementationType == typeof(RelationalDmlTargetCommitWitness)
            && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(enabled, descriptor =>
            descriptor.ServiceType == typeof(TypedDmlRuntime));

        Assert.Throws<InvalidOperationException>(() => builder.AddHsSqlAgentTargetCommitWitness());
    }
    [Fact]
    public void ExecutionFenceRegistration_AutomaticallyIncludesWitnessAndIsSingleRegistration()
    {
        var services = new ServiceCollection();
        var builder = services.AddHsSqlAgentCore(V3TestEncryption.Prepare(new HsSqlAgentServiceOptions())).AddHsSqlAgentTargetExecutionFence();
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IDmlTargetExecutionFence)
            && descriptor.ImplementationType == typeof(RelationalDmlTargetExecutionFence)
            && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IDmlTargetCommitWitness)
            && descriptor.ImplementationType == typeof(RelationalDmlTargetCommitWitness));
        Assert.Throws<InvalidOperationException>(() => builder.AddHsSqlAgentTargetExecutionFence());
    }

}
