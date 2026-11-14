using System.Collections.Immutable;
using Xunit;

namespace SqlAgent.Test.Services;

public class DmlFingerprintServiceTests
{
    [Fact]
    public void PlanFingerprint_IsStableForSameImmutableCommand()
    {
        var command = new CompiledSqlCommand(
            "UPDATE users SET active = @p0 WHERE id = @p1",
            [new SqlParameterValue("p0", true), new SqlParameterValue("p1", 42)],
            SqlStatementKind.Update,
            "legacy",
            SqlAgentToolType.Postgres);

        var first = DmlFingerprintService.ComputePlanFingerprint(command, "policy-v1");
        var second = DmlFingerprintService.ComputePlanFingerprint(command, "policy-v1");

        Assert.Equal(first, second);
    }

    [Fact]
    public void PlanFingerprint_ChangesWhenPolicyOrParameterChanges()
    {
        var command = new CompiledSqlCommand(
            "DELETE FROM users WHERE id = @p0",
            [new SqlParameterValue("p0", 42)],
            SqlStatementKind.Delete,
            "legacy",
            SqlAgentToolType.Postgres);
        var changed = new CompiledSqlCommand(
            command.Sql,
            ImmutableArray.Create(new SqlParameterValue("p0", 43)),
            command.Kind,
            command.PlanFingerprint,
            command.TargetProvider);

        var baseline = DmlFingerprintService.ComputePlanFingerprint(command, "policy-v1");

        Assert.NotEqual(baseline, DmlFingerprintService.ComputePlanFingerprint(changed, "policy-v1"));
        Assert.NotEqual(baseline, DmlFingerprintService.ComputePlanFingerprint(command, "policy-v2"));
    }

    [Fact]
    public void RowSetFingerprint_ChangesWhenMatchedIdentityChangesEvenAtSameCount()
    {
        var approved = DmlFingerprintService.ComputeRowSetFingerprint(
            new IReadOnlyList<object?>[] { new object?[] { 1 }, new object?[] { 2 } });
        var current = DmlFingerprintService.ComputeRowSetFingerprint(
            new IReadOnlyList<object?>[] { new object?[] { 3 }, new object?[] { 4 } });

        Assert.NotEqual(approved, current);
    }
}
