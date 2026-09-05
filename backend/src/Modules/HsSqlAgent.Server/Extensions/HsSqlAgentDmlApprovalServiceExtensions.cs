using HsSqlAgent.Approvals;

namespace HsSqlAgent.Server.Extensions;

public static class HsSqlAgentDmlApprovalServiceExtensions
{
    /// <summary>
    /// Replaces the default MCP Elicitation approval route with one host-selected DML approval
    /// provider. The provider controls only the approval decision; HsSqlAgent still owns preview,
    /// evidence binding, policy enforcement, revalidation, and commit.
    /// </summary>
    public static HsSqlAgentRegistrationBuilder AddHsSqlAgentDmlApproval<TProvider>(
        this HsSqlAgentRegistrationBuilder builder,
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TProvider : class, IDmlApprovalProvider
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (builder.IsRegistered("dml-approval-provider"))
            throw new InvalidOperationException("A HsSqlAgent DML approval provider is already registered.");

        builder.AddHsSqlAgentRuntime();
        if (!builder.TryRegister("dml-approval-provider")) return builder;

        builder.Services.Add(new ServiceDescriptor(
            typeof(IDmlApprovalProvider),
            typeof(TProvider),
            lifetime));
        return builder;
    }
}
