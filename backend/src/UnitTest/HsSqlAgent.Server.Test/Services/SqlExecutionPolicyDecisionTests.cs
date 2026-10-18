using Admin.Service.Models;
using HsSqlAgent.Server.Services;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public sealed class SqlExecutionPolicyDecisionTests
{
    private static readonly DmlApprovalExecutionContext Principal =
        new("mcp-key:7", "db-management:11", SqlAgentToolType.Postgres, "orders");

    [Fact]
    public void PolicyAndTables_AreDefensivelyFrozen()
    {
        var policy = new SecurityPolicyModel { QueryMaxRows = 35, QueryTimeoutSeconds = 8 };
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.orders" };
        var decision = SqlExecutionPolicyDecision.ForDml(Principal, policy, tables);
        var version = decision.PolicyVersion;

        policy.QueryMaxRows = 999;
        tables.Clear();
        decision.PolicySnapshot.DmlMaxAffectedRows = 100000;

        Assert.Equal(version, decision.PolicyVersion);
        Assert.Equal(35, decision.PolicySnapshot.QueryMaxRows);
        Assert.NotEqual(100000, decision.PolicySnapshot.DmlMaxAffectedRows);
        Assert.Contains("PUBLIC.ORDERS", decision.AllowedTables!);
        Assert.Single(decision.AllowedTables!);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void DmlDecision_UsesCompilerPolicyVersionForEachProvider(SqlAgentToolType provider)
    {
        var identity = Principal with { Provider = provider };
        var policy = new SecurityPolicyModel { DmlMaxAffectedRows = 7 };
        var tables = new HashSet<string> { "public.orders" };
        var decision = SqlExecutionPolicyDecision.ForDml(identity, policy, tables);
        Assert.Equal(TypedDmlRuntime.ComputePolicyVersion(policy, tables), decision.PolicyVersion);
    }

    [Fact]
    public void PolicyRevalidation_DeniesChangedTableScope()
    {
        var policy = new SecurityPolicyModel();
        var initial = SqlExecutionPolicyDecision.ForDml(
            Principal, policy, new HashSet<string> { "orders" });
        var changed = SqlExecutionPolicyDecision.ForDml(
            Principal, policy, new HashSet<string> { "orders", "secrets" });
        Assert.Throws<UnauthorizedAccessException>(() => initial.EnsureSameDmlDecision(changed));
    }

    [Fact]
    public void PolicyRevalidation_DeniesUpdatedPolicy()
    {
        var policy = new SecurityPolicyModel { DmlMaxAffectedRows = 50 };
        var initial = SqlExecutionPolicyDecision.ForDml(Principal, policy, null);
        policy.DmlMaxAffectedRows = 500;
        var changed = SqlExecutionPolicyDecision.ForDml(Principal, policy, null);
        Assert.Throws<UnauthorizedAccessException>(() => initial.EnsureSameDmlDecision(changed));
    }

    [Fact]
    public void PolicyRevalidation_DeniesChangedIdentityAndProvider()
    {
        var policy = new SecurityPolicyModel();
        var initial = SqlExecutionPolicyDecision.ForDml(Principal, policy, null);
        foreach (var next in new[]
        {
            Principal with { PrincipalIdentity = "mcp-key:9" },
            Principal with { TargetIdentity = "db-management:99" },
            Principal with { DatabaseIdentity = "other" },
            Principal with { Provider = SqlAgentToolType.MySQL }
        })
        {
            var changed = SqlExecutionPolicyDecision.ForDml(next, policy, null);
            Assert.Throws<UnauthorizedAccessException>(() => initial.EnsureSameDmlDecision(changed));
        }
    }

    [Fact]
    public void IdenticalRevalidation_IsAllowed_AndQueryDecisionCannotBeUsedForDml()
    {
        var policy = new SecurityPolicyModel();
        var initial = SqlExecutionPolicyDecision.ForDml(Principal, policy, null);
        var same = SqlExecutionPolicyDecision.ForDml(Principal, policy.Clone(), null);
        initial.EnsureSameDmlDecision(same);
        Assert.NotEmpty(initial.PolicyVersion);
    }
}
