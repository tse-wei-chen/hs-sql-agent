using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreDmlConstantPredicatePolicyTests
{
    private const string DiagnosticKey = "HsSqlAgent.SqlCore.Diagnostic";

    public static IEnumerable<object[]> DeepPolicyCases()
    {
        foreach (var provider in Enum.GetValues<SqlAgentToolType>())
        foreach (var depth in new[] { 128, 512, 2048 })
        foreach (var selective in new[] { false, true })
            yield return new object[] { provider, depth, selective };
    }

    [Theory]
    [MemberData(nameof(DeepPolicyCases))]
    public void DeepMutationPredicate_RequiresSamePolicyDecisionOn256KiB(
        SqlAgentToolType provider, int depth, bool selective)
    {
        var predicate = string.Join(" AND ", Enumerable.Repeat("1 = 1", depth));
        if (selective) predicate += " AND id = 7";
        Exception? failure = null;
        string? sql = null;
        var worker = new Thread(() =>
        {
            try
            {
                sql = HsSqlAgent.SqlCore.SqlCoreFacade.CompileDml(
                    "DELETE FROM users WHERE " + predicate, provider, provider,
                    new SqlPlanValidationContext("deep-policy-proof-v1")).Sql;
            }
            catch (Exception error) { failure = error; }
        }, 262144);
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(20)), "Deep policy worker timed out");
        if (selective)
        {
            Assert.Null(failure);
            Assert.False(string.IsNullOrEmpty(sql));
        }
        else
        {
            var error = Assert.IsType<UnauthorizedAccessException>(failure);
            var diagnostic = Assert.IsType<SqlDiagnostic>(error.Data[DiagnosticKey]);
            Assert.Equal("SQL_POLICY_DELETE_UNBOUNDED_PREDICATE", diagnostic.Code);
            Assert.Equal(SqlDiagnosticStage.Policy, diagnostic.Stage);
        }
    }

    public static IEnumerable<object[]> AlwaysTrueCases()
    {
        var predicates = new[]
        {
            "1 = 1",
            "1 = 1 OR id = 7",
            "id = 7 OR 2 = 2",
            "NOT (1 = 0)",
            "(3 = 3) AND (4 = 4)"
        };

        foreach (var dialect in Enum.GetValues<SqlAgentToolType>())
        {
            foreach (var predicate in predicates)
            {
                yield return new object[]
                {
                    dialect,
                    $"UPDATE users SET name = 'Blocked' WHERE {predicate}",
                    "SQL_POLICY_UPDATE_UNBOUNDED_PREDICATE"
                };
                yield return new object[]
                {
                    dialect,
                    $"DELETE FROM users WHERE {predicate}",
                    "SQL_POLICY_DELETE_UNBOUNDED_PREDICATE"
                };
            }
        }
    }

    [Theory]
    [MemberData(nameof(AlwaysTrueCases))]
    public void DefaultDmlPolicy_RejectsProvablyUnboundedPredicates(
        SqlAgentToolType dialect, string sql, string expectedCode)
    {
        var parsed = CoreSqlTextParser.ParseDml(sql, dialect);
        var ex = Assert.Throws<UnauthorizedAccessException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed, dialect, new SqlPlanValidationContext("predicate-proof-v1")));

        var diagnostic = Assert.IsType<SqlDiagnostic>(ex.Data[DiagnosticKey]);
        Assert.Equal(expectedCode, diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.Policy, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Policy, diagnostic.Category);
        Assert.NotNull(diagnostic.Span);
        Assert.True(diagnostic.Span.Length >= 0);
    }

    [Theory]
    [InlineData("UPDATE users SET name = 'Allowed' WHERE id = 7 AND 1 = 1")]
    [InlineData("DELETE FROM users WHERE id = 7 OR 1 = 0")]
    [InlineData("DELETE FROM users WHERE 1 = 0")]
    [InlineData("UPDATE users SET name = 'Allowed' WHERE id = id")]
    public void DefaultDmlPolicy_DoesNotMistakeUnknownOrFalsePredicatesForAlwaysTrue(
        string sql)
    {
        foreach (var dialect in Enum.GetValues<SqlAgentToolType>())
        {
            var parsed = CoreSqlTextParser.ParseDml(sql, dialect);
            var command = CoreDmlCompiler.CreateDefault().Compile(
                parsed, dialect, new SqlPlanValidationContext("predicate-proof-v1"));
            Assert.False(string.IsNullOrWhiteSpace(command.Sql));
        }
    }

    [Theory]
    [InlineData("UPDATE users SET name = 'Allowed' WHERE 1 = 1")]
    [InlineData("DELETE FROM users WHERE 1 = 1")]
    public void ExplicitFullTableAuthorization_CanCompileProvablyTruePredicates(string sql)
    {
        var policy = new DmlCompilationPolicy(false, false, true, true);
        foreach (var dialect in Enum.GetValues<SqlAgentToolType>())
        {
            var parsed = CoreSqlTextParser.ParseDml(sql, dialect);
            var command = CoreDmlCompiler.CreateDefault().Compile(
                parsed, dialect, new SqlPlanValidationContext("explicit-full-table-v1"), policy);
            Assert.False(string.IsNullOrWhiteSpace(command.Sql));
        }
    }
}
