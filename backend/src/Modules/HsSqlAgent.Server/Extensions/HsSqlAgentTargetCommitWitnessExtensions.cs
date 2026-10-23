using SqlAgent.Service.Core.Execution;

namespace HsSqlAgent.Server.Extensions;

/// <summary>
/// Optional target-database transactional COMMIT witness for durable DML.
/// Provision the target witness table and least-privilege INSERT/SELECT grants
/// BEFORE enabling this registration. This is not a worker fencing mechanism.
/// </summary>
public static class HsSqlAgentTargetCommitWitnessExtensions
{
    public static HsSqlAgentRegistrationBuilder AddHsSqlAgentTargetCommitWitness(
        this HsSqlAgentRegistrationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (builder.IsRegistered("target-dml-commit-witness"))
            throw new InvalidOperationException("Target DML commit witness is already registered.");

        builder.AddHsSqlAgentRuntime();
        if (!builder.TryRegister("target-dml-commit-witness")) return builder;

        builder.Services.AddSingleton<IDmlTargetCommitWitness, RelationalDmlTargetCommitWitness>();
        return builder;
    }
}
