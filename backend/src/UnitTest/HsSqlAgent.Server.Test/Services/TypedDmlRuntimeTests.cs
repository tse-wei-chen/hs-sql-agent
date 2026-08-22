using System.Collections.Immutable;
using Admin.Service.Models;
using HsSqlAgent.Server.Services;
using Moq;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using SqlAgent.Service.Strategies;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public class TypedDmlRuntimeTests
{
    [Fact]
    public async Task PreviewAsync_InsertFailsClosedBeforeProviderAccess()
    {
        var strategy = new Mock<ISqlStrategy>(MockBehavior.Strict);
        var runtime = new TypedDmlRuntime();
        var definition = new DmlDefinition
        {
            Operation = DmlOperation.Insert,
            TableName = "public.users",
            Values = [new NameValuePair { FieldName = "name", Value = "Alice" }]
        };

        var error = await Assert.ThrowsAsync<NotSupportedException>(() =>
            runtime.PreviewAsync(
                strategy.Object,
                "connection",
                definition,
                new SecurityPolicyModel(),
                null,
                TestContext.Current.CancellationToken));

        Assert.Contains("INSERT", error.Message, StringComparison.OrdinalIgnoreCase);
        strategy.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CommitAsync_RejectsPolicyChangeBeforeProviderAccess()
    {
        var previewPolicy = new SecurityPolicyModel
        {
            RequireWhereForUpdate = true,
            DmlMaxAffectedRows = 25
        };
        var policyVersion = TypedDmlRuntime.ComputePolicyVersion(previewPolicy, null);
        var command = new CompiledSqlCommand(
            "UPDATE public.users SET status = @p0 WHERE id = @p1",
            ImmutableArray<SqlParameterValue>.Empty,
            SqlStatementKind.Update,
            "command-fingerprint",
            SqlAgentToolType.Postgres);
        var match = new CompiledSqlCommand(
            "SELECT id FROM public.users WHERE id = @p0",
            ImmutableArray<SqlParameterValue>.Empty,
            SqlStatementKind.Select,
            "match-fingerprint",
            SqlAgentToolType.Postgres);
        var plan = new ValidatedDmlPlan(
            DmlOperation.Update,
            "public.users",
            command,
            match,
            ImmutableArray.Create("id"),
            DmlRowIdentityAssurance.Strict,
            "plan-fingerprint",
            policyVersion,
            TimeSpan.FromMinutes(5),
            25);
        var now = DateTimeOffset.UtcNow;
        var challenge = new DmlApprovalChallenge(
            "plan-fingerprint",
            "rowset-fingerprint",
            1,
            policyVersion,
            now,
            now.AddMinutes(5),
            "nonce");
        var session = new TypedDmlApprovalSession(
            plan,
            new DmlPreview(
                DmlOperation.Update,
                "public.users",
                1,
                ImmutableArray<IReadOnlyDictionary<string, object?>>.Empty,
                challenge));
        var changedPolicy = previewPolicy.Clone();
        changedPolicy.DmlMaxAffectedRows = 10;
        var strategy = new Mock<ISqlStrategy>(MockBehavior.Strict);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new TypedDmlRuntime().CommitAsync(
                strategy.Object,
                "connection",
                session,
                changedPolicy,
                null,
                TestContext.Current.CancellationToken));

        Assert.Contains("security policy", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("new preview", error.Message, StringComparison.OrdinalIgnoreCase);
        strategy.VerifyNoOtherCalls();
    }

    [Fact]
    public void ComputePolicyVersion_IsStableAcrossWhitelistOrder()
    {
        var policy = new SecurityPolicyModel
        {
            RequireWhereForUpdate = true,
            RequireWhereForDelete = true,
            AllowFullTableUpdate = false,
            AllowFullTableDelete = false,
            DmlMaxAffectedRows = 25,
            UpdatedAt = new DateTime(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc)
        };

        var left = TypedDmlRuntime.ComputePolicyVersion(
            policy,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "public.users",
                "public.orders"
            });
        var right = TypedDmlRuntime.ComputePolicyVersion(
            policy,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "public.orders",
                "public.users"
            });

        Assert.Equal(left, right);
    }

    [Fact]
    public void ComputePolicyVersion_ChangesWhenDmlPolicyChanges()
    {
        var original = new SecurityPolicyModel
        {
            RequireWhereForUpdate = true,
            RequireWhereForDelete = true,
            DmlMaxAffectedRows = 25
        };
        var changed = original.Clone();
        changed.DmlMaxAffectedRows = 26;

        var before = TypedDmlRuntime.ComputePolicyVersion(original, null);
        var after = TypedDmlRuntime.ComputePolicyVersion(changed, null);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void ComputePolicyVersion_ChangesWhenWhitelistChanges()
    {
        var policy = new SecurityPolicyModel { DmlMaxAffectedRows = 25 };

        var before = TypedDmlRuntime.ComputePolicyVersion(
            policy,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.users" });
        var after = TypedDmlRuntime.ComputePolicyVersion(
            policy,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "public.users",
                "public.orders"
            });

        Assert.NotEqual(before, after);
    }
}
