using System.Reflection;
using HsSqlAgent.Server.Models;
using HsSqlAgent.SqlCore.Enums;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace HsSqlAgent.Server.Test.Contracts;

/// <summary>
/// Release-surface fence. v3.x additions must use independent capability
/// interfaces instead of growing mandatory methods on existing integrations.
/// </summary>
public sealed class V3PublicContractFreezeTests
{
    [Fact]
    public void AtomicApprovalStore_RequiredMethodsStayStable()
    {
        var methods = typeof(IDmlApprovalChallengeStore)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(x => x.Name)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            ["RegisterAsync", "TryConsumeAsync", "TryPromoteAsync"],
            methods);
    }

    [Fact]
    public void TargetFence_RequiredMethodsStayStable()
    {
        var methods = typeof(IDmlTargetExecutionFence)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(x => x.Name)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["AdmitAsync", "ReadAsync", "RevokeAsync"], methods);
    }

    [Fact]
    public void ApprovalChallenge_PreservesOriginalPositionalConstructor()
    {
        var signature = new[]
        {
            typeof(string), typeof(string), typeof(int), typeof(string),
            typeof(string), typeof(DateTimeOffset), typeof(DateTimeOffset), typeof(string)
        };
        var constructor = typeof(DmlApprovalChallenge).GetConstructor(signature);
        Assert.NotNull(constructor);
        Assert.NotNull(typeof(DmlApprovalChallenge).GetProperty("ApprovalPlanBinding"));
    }

    [Fact]
    public void McpUnknownOutcome_PreservesExistingBinaryFactoryOverload()
    {
        Assert.NotNull(typeof(McpDmlToolResult).GetMethod(
            nameof(McpDmlToolResult.OutcomeUnknown),
            [typeof(string), typeof(int), typeof(long), typeof(string), typeof(string)]));
        Assert.NotNull(typeof(McpDmlToolResult).GetMethod(
            nameof(McpDmlToolResult.OutcomeUnknown),
            [typeof(string), typeof(int), typeof(long), typeof(string),
                typeof(string), typeof(string)]));
    }
}
