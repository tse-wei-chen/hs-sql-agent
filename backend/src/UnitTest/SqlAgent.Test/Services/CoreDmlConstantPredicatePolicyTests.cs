using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreDmlConstantPredicatePolicyTests
{
    private const string DiagnosticKey = "HsSqlAgent.SqlCore.Diagnostic";

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
