using SqlAgent.Service.Core.Execution;

namespace HsSqlAgent.Server.Extensions;

/// <summary>
/// Advanced opt-in to a durable pre-mutation target-admission barrier.
/// Requires transactional target witness and a separately provisioned target
/// fence table with an enforced unique REQUEST_ID primary key.
/// </summary>
public static class HsSqlAgentTargetExecutionFenceExtensions
{
    public static HsSqlAgentRegistrationBuilder AddHsSqlAgentTargetExecutionFence(
        this HsSqlAgentRegistrationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (builder.IsRegistered("target-dml-execution-fence"))
            throw new InvalidOperationException("Target DML execution fence is already registered.");

        if (!builder.IsRegistered("target-dml-commit-witness"))
            builder.AddHsSqlAgentTargetCommitWitness();
        if (!builder.TryRegister("target-dml-execution-fence")) return builder;

        builder.Services.AddSingleton<IDmlTargetExecutionFence, RelationalDmlTargetExecutionFence>();
        return builder;
    }
}
