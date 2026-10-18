using HsSqlAgent.SqlCore;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreColumnAuthorizationPolicyTests
{
    public static TheoryData<SqlAgentToolType> Providers => new()
    {
        SqlAgentToolType.Postgres, SqlAgentToolType.MySQL,
        SqlAgentToolType.MsSqlServer, SqlAgentToolType.Sqlite,
        SqlAgentToolType.Oracle, SqlAgentToolType.Firebird
    };

    private static SqlPlanValidationContext Restricted(params string[] names) =>
        new SqlPlanValidationContext("query-column-auth-v1",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders" })
            .WithAllowedQueryColumns(names.ToHashSet(StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(Providers))]
    public void ExplicitAuthorizedColumns_CompileAcrossSixProviders(SqlAgentToolType provider)
    {
        var result = SqlCoreFacade.CompileQuery(
            "SELECT id FROM orders WHERE id = 4 ORDER BY id", provider, provider,
            Restricted("id"), new SqlExecutionPlanPolicy(25));
        Assert.NotEmpty(result.Sql);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void UnauthorizedWhereColumn_IsDeniedWithFacts(SqlAgentToolType provider)
    {
        var err = Assert.Throws<UnauthorizedAccessException>(() =>
            SqlCoreFacade.CompileQueryWithFacts(
                "SELECT id FROM orders WHERE secret = 'x'",
                provider, provider, Restricted("id"), new SqlExecutionPlanPolicy(25)));
        Assert.Contains("not authorized", err.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(SqlCoreInspection.TryGetQueryFactsFromException(err));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void WildcardAndJoins_AreFailClosedAcrossSixProviders(SqlAgentToolType provider)
    {
        Assert.Throws<UnauthorizedAccessException>(() => SqlCoreFacade.CompileQuery(
            "SELECT * FROM orders", provider, provider,
            Restricted("id"), new SqlExecutionPlanPolicy(25)));
        Assert.Throws<UnauthorizedAccessException>(() => SqlCoreFacade.CompileQuery(
            "SELECT o.id FROM orders o JOIN customers c ON c.id = o.id", provider, provider,
            Restricted("id"), new SqlExecutionPlanPolicy(25)));
    }

    [Fact]
    public void ChangingColumnSet_ChangesPolicyVersionWithoutMutatingExistingScope()
    {
        var set = new HashSet<string> { "id" };
        var first = new SqlPlanValidationContext("version-1").WithAllowedQueryColumns(set);
        set.Add("secret");
        Assert.DoesNotContain("secret", first.AllowedQueryColumns!);
        var second = new SqlPlanValidationContext("version-1").WithAllowedQueryColumns(set);
        Assert.NotEqual(first.PolicyVersion, second.PolicyVersion);
    }

    [Fact]
    public void EmptyColumns_AreFailClosedAndDefaultBehaviorUnchanged()
    {
        Assert.Throws<UnauthorizedAccessException>(() => SqlCoreFacade.CompileQuery(
            "SELECT id FROM orders", SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            Restricted(), new SqlExecutionPlanPolicy(25)));
        Assert.NotEmpty(SqlCoreFacade.CompileQuery(
            "SELECT * FROM orders", SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("legacy"), new SqlExecutionPlanPolicy(25)).Sql);
    }
}
