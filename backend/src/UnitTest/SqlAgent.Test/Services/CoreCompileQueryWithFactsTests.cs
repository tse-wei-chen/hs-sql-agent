using HsSqlAgent.SqlCore;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreCompileQueryWithFactsTests
{
    public static TheoryData<SqlAgentToolType> Providers => new()
    {
        SqlAgentToolType.Postgres,
        SqlAgentToolType.MySQL,
        SqlAgentToolType.MsSqlServer,
        SqlAgentToolType.Sqlite,
        SqlAgentToolType.Oracle,
        SqlAgentToolType.Firebird
    };

    [Theory]
    [MemberData(nameof(Providers))]
    public void CompileQueryWithFacts_MatchesExistingCompileAndInspectionContracts(SqlAgentToolType provider)
    {
        const string sql = "SELECT id FROM users WHERE id = 7";
        var validation = new SqlPlanValidationContext(
            "single-parse-contract-v1",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" });
        var policy = new SqlExecutionPlanPolicy(25);

        var combined = SqlCoreFacade.CompileQueryWithFacts(sql, provider, provider, validation, policy);
        var command = SqlCoreFacade.CompileQuery(sql, provider, provider, validation, policy);
        var facts = SqlCoreInspection.GetQueryFacts(sql, provider);

        Assert.Equal(command.Sql, combined.Command.Sql);
        Assert.Equal(command.PlanFingerprint, combined.Command.PlanFingerprint);
        Assert.Equal(command.Kind, combined.Command.Kind);
        Assert.Equal(command.Parameters.Count, combined.Command.Parameters.Count);
        Assert.Equal(
            facts.ReferencedTables.OrderBy(value => value, StringComparer.OrdinalIgnoreCase),
            combined.Facts.ReferencedTables.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(facts.ContainsCte, combined.Facts.ContainsCte);
        Assert.Equal(facts.ContainsSubquery, combined.Facts.ContainsSubquery);
    }

    [Fact]
    public void CompileQueryWithFacts_InspectsNestedCteAndSubqueryFromCompiledBoundDocument()
    {
        const string sql =
            "WITH active AS (SELECT id FROM public.users) " +
            "SELECT a.id FROM active a WHERE EXISTS (SELECT 1 FROM public.orders o WHERE o.user_id = a.id)";
        var validation = new SqlPlanValidationContext(
            "single-parse-nested-v1",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.users", "public.orders" });

        var result = SqlCoreFacade.CompileQueryWithFacts(
            sql,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            validation,
            new SqlExecutionPlanPolicy(50));

        Assert.Contains("public.users", result.Facts.ReferencedTables, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("public.orders", result.Facts.ReferencedTables, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("active", result.Facts.ReferencedTables, StringComparer.OrdinalIgnoreCase);
        Assert.True(result.Facts.ContainsCte);
        Assert.True(result.Facts.ContainsSubquery);
    }
}
