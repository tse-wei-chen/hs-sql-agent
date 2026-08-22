using Admin.Service.Models;
using HsSqlAgent.Server.Services;
using Moq;
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
}
