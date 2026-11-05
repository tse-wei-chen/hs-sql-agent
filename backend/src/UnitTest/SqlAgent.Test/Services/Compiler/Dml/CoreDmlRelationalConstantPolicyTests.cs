using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Proves that safe constant numeric order comparisons cannot disguise full-table
/// UPDATE/DELETE, while uncertain, NULL-dependent and selective predicates remain
/// outside this deliberately narrow proof.
/// </summary>
public sealed class CoreDmlRelationalConstantPolicyTests
{
    private const string DiagnosticKey = "HsSqlAgent.SqlCore.Diagnostic";

    public static IEnumerable<object[]> ProvenUnbounded()
    {
        string[] predicates =
        [
            "1 < 2",
            "2 <= 2",
            "5 > 3",
            "5 >= 5",
            "1.5 < 2.5",
            "2.5 >= 2.5",
            "NOT (5 < 3)",
            "1 < 2 OR id = 7",
            "id = 7 OR 3 > 2",
            "(1 <= 2) AND (5 >= 5)"
        ];

        foreach (var provider in Enum.GetValues<SqlAgentToolType>())
        foreach (var predicate in predicates)
        {
            yield return [provider, $"UPDATE users SET name = 'Blocked' WHERE {predicate}",
                "SQL_POLICY_UPDATE_UNBOUNDED_PREDICATE"];
            yield return [provider, $"DELETE FROM users WHERE {predicate}",
                "SQL_POLICY_DELETE_UNBOUNDED_PREDICATE"];
        }
    }

    [Theory]
    [MemberData(nameof(ProvenUnbounded))]
    public void DefaultPolicy_DeniesNumericRelationalTautologies(
        SqlAgentToolType provider, string sql, string expectedCode)
    {
        var parsed = CoreSqlTextParser.ParseDml(sql, provider);
        var error = Assert.Throws<UnauthorizedAccessException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed, provider, new SqlPlanValidationContext("relational-proof-v1")));

        var diagnostic = Assert.IsType<SqlDiagnostic>(error.Data[DiagnosticKey]);
        Assert.Equal(expectedCode, diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.Policy, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Policy, diagnostic.Category);
        Assert.NotNull(diagnostic.Span);
        Assert.True(diagnostic.Span.Length > 0);
    }

    public static IEnumerable<object[]> NotProvenUnbounded()
    {
        string[] predicates =
        [
            "1 > 2",
            "2 >= 3",
            "5 < 3",
            "5 <= 4",
            "2.5 < 1.5",
            "id = 7 AND 1 < 2",
            "id = 7 OR 1 > 2",
            "id > 0",
            "id = id",
            "id IS NULL OR 3 < 2",
            "NULL = NULL"
        ];

        foreach (var provider in Enum.GetValues<SqlAgentToolType>())
        foreach (var predicate in predicates)
        {
            yield return [provider, $"UPDATE users SET name = 'Allowed' WHERE {predicate}"];
            yield return [provider, $"DELETE FROM users WHERE {predicate}"];
        }
    }

    [Theory]
    [MemberData(nameof(NotProvenUnbounded))]
    public void DefaultPolicy_DoesNotClaimUnknownOrFalseIsAlwaysTrue(
        SqlAgentToolType provider, string sql)
    {
        var parsed = CoreSqlTextParser.ParseDml(sql, provider);
        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed, provider, new SqlPlanValidationContext("relational-proof-v1"));
        Assert.False(string.IsNullOrWhiteSpace(command.Sql));
    }

    [Theory]
    [InlineData("UPDATE users SET name = 'Allowed' WHERE 1 < 2")]
    [InlineData("DELETE FROM users WHERE 5 >= 5")]
    public void ExplicitFullTablePermission_StillCompilesNumericTautology(string sql)
    {
        var policy = new DmlCompilationPolicy(false, false, true, true);
        foreach (var provider in Enum.GetValues<SqlAgentToolType>())
        {
            var parsed = CoreSqlTextParser.ParseDml(sql, provider);
            var command = CoreDmlCompiler.CreateDefault().Compile(
                parsed, provider, new SqlPlanValidationContext("explicit-relational-v1"), policy);
            Assert.False(string.IsNullOrWhiteSpace(command.Sql));
        }
    }
}
