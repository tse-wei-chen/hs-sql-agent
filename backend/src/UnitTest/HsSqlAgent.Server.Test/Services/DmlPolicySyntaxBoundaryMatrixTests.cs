using HsSqlAgent.Server.Services;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public sealed class DmlPolicySyntaxBoundaryMatrixTests
{
    public static IEnumerable<object[]> SixDialectDmlPolicyBoundaryMatrix()
    {
        foreach (var dialect in Enum.GetValues<SqlAgentToolType>())
        {
            yield return
            [
                dialect,
                "update-without-where",
                "UPDATE users SET name = 'Blocked'",
                "SQL_POLICY_UPDATE_REQUIRES_WHERE"
            ];

            yield return
            [
                dialect,
                "delete-without-where",
                "DELETE FROM users",
                "SQL_POLICY_DELETE_REQUIRES_WHERE"
            ];
        }
    }

    [Fact]
    public void SixDialectDmlPolicyBoundaryMatrix_HasStableCoverage()
    {
        var cases = SixDialectDmlPolicyBoundaryMatrix().ToArray();

        Assert.Equal(12, cases.Length);
        foreach (var dialect in Enum.GetValues<SqlAgentToolType>())
        {
            Assert.Equal(
                2,
                cases.Count(item => Equals(item[0], dialect)));
        }
    }

    [Theory]
    [MemberData(nameof(SixDialectDmlPolicyBoundaryMatrix))]
    public async Task TypedDmlRuntime_PreservesPolicyDiagnosticAcrossServerBoundary(
        SqlAgentToolType dialect,
        string scenario,
        string sql,
        string diagnosticCode)
    {
        var fixture = SyntaxBoundaryTestSupport.DmlRowSetProvider(dialect);
        var parsed = CoreSqlTextParser.ParseDml(sql, dialect);
        var policy = SyntaxBoundaryTestSupport.Policy();
        IReadOnlySet<string> allowedTables =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                fixture.QualifiedTable
            };
        var approvalContext = new DmlApprovalExecutionContext(
            "syntax-policy-principal",
            "syntax-policy-target",
            dialect,
            "syntax-policy-db");

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new TypedDmlRuntime().PreviewAsync(
                fixture.Provider.Object,
                "Data Source=:memory:",
                parsed,
                policy,
                allowedTables,
                approvalContext,
                TestContext.Current.CancellationToken));

        var diagnostic =
            error.Data["HsSqlAgent.SqlCore.Diagnostic"]
            as SqlDiagnostic;

        Assert.NotNull(diagnostic);
        Assert.Equal(diagnosticCode, diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.Policy, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Policy, diagnostic.Category);
        Assert.False(
            string.IsNullOrWhiteSpace(diagnostic.Message),
            scenario);
        Assert.NotNull(diagnostic.Span);
        Assert.True(diagnostic.Span.Start >= 0, scenario);
        Assert.True(diagnostic.Span.Length >= 0, scenario);

        Assert.Equal(1, fixture.Connections.CreateCount);
        fixture.Metadata.VerifyAll();
    }
}
