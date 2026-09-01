using Admin.Service.Models;
using HsSqlAgent.Server.Services;
using Moq;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public sealed class DmlSyntaxBoundaryMatrixTests
{
    private sealed record DialectSpec(
        SqlAgentToolType Dialect,
        string BasicSql,
        string MultiRowSql,
        string QuotedSql,
        string RenderedTableMarker);

    private static readonly DialectSpec[] Dialects =
    [
        new(
            SqlAgentToolType.Postgres,
            "INSERT INTO users (id, name) VALUES (1, 'Alice')",
            "INSERT INTO users (id, name) VALUES (1, 'Alice'), (2, 'Bob')",
            "INSERT INTO \"users\" (\"id\", \"name\") VALUES (1, 'Alice')",
            "\"users\""),
        new(
            SqlAgentToolType.MySQL,
            "INSERT INTO users (id, name) VALUES (1, 'Alice')",
            "INSERT INTO users (id, name) VALUES (1, 'Alice'), (2, 'Bob')",
            "INSERT INTO `users` (`id`, `name`) VALUES (1, 'Alice')",
            "`users`"),
        new(
            SqlAgentToolType.MsSqlServer,
            "INSERT INTO users (id, name) VALUES (1, 'Alice')",
            "INSERT INTO users (id, name) VALUES (1, 'Alice'), (2, 'Bob')",
            "INSERT INTO [users] ([id], [name]) VALUES (1, 'Alice')",
            "[users]"),
        new(
            SqlAgentToolType.Sqlite,
            "INSERT INTO users (id, name) VALUES (1, 'Alice')",
            "INSERT INTO users (id, name) VALUES (1, 'Alice'), (2, 'Bob')",
            "INSERT INTO \"users\" (\"id\", \"name\") VALUES (1, 'Alice')",
            "\"users\""),
        new(
            SqlAgentToolType.Oracle,
            "INSERT INTO users (id, name) VALUES (1, 'Alice')",
            "INSERT INTO users (id, name) VALUES (1, 'Alice'), (2, 'Bob')",
            "INSERT INTO \"USERS\" (\"ID\", \"NAME\") VALUES (1, 'Alice')",
            "\"USERS\""),
        new(
            SqlAgentToolType.Firebird,
            "INSERT INTO users (id, name) VALUES (1, 'Alice')",
            "INSERT INTO users (id, name) VALUES (1, 'Alice'), (2, 'Bob')",
            "INSERT INTO \"USERS\" (\"ID\", \"NAME\") VALUES (1, 'Alice')",
            "\"USERS\"")
    ];

    public static IEnumerable<object[]> SixDialectInsertValuesBoundaryMatrix()
    {
        foreach (var spec in Dialects)
        {
            yield return Case(
                spec.Dialect,
                "explicit-single-row",
                spec.BasicSql,
                1,
                spec.RenderedTableMarker);

            yield return Case(
                spec.Dialect,
                "explicit-multi-row",
                spec.MultiRowSql,
                2,
                spec.RenderedTableMarker);

            yield return Case(
                spec.Dialect,
                "quoted-explicit-row",
                spec.QuotedSql,
                1,
                spec.RenderedTableMarker);
        }
    }

    public static IEnumerable<object[]> SixDialectInsertSelectFailClosedMatrix()
    {
        foreach (var spec in Dialects)
        {
            yield return
            [
                spec.Dialect,
                "INSERT INTO users (id, name) SELECT id, name FROM staged_users"
            ];
        }
    }

    [Fact]
    public void SixDialectInsertValuesBoundaryMatrix_HasStableCoverage()
    {
        var cases = SixDialectInsertValuesBoundaryMatrix().ToArray();

        Assert.Equal(18, cases.Length);
        foreach (var dialect in Enum.GetValues<SqlAgentToolType>())
        {
            Assert.Equal(
                3,
                cases.Count(item => Equals(item[0], dialect)));
        }
    }

    [Theory]
    [MemberData(nameof(SixDialectInsertValuesBoundaryMatrix))]
    public async Task TypedDmlRuntime_PreviewsSixDialectInsertGrammarThroughRealFSharpBoundary(
        SqlAgentToolType dialect,
        string scenario,
        string sql,
        int expectedRows,
        string renderedTableMarker)
    {
        var fixture = SyntaxBoundaryTestSupport.DmlProvider(dialect);
        var parsed = CoreSqlTextParser.ParseDml(sql, dialect);
        var policy = SyntaxBoundaryTestSupport.Policy();
        policy.DmlMaxAffectedRows = expectedRows;
        IReadOnlySet<string> allowedTables =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                fixture.QualifiedTable
            };
        var approvalContext = new DmlApprovalExecutionContext(
            "syntax-matrix-principal",
            "syntax-matrix-target",
            dialect,
            "syntax-matrix-db");

        var session = await new TypedDmlRuntime().PreviewAsync(
            fixture.Provider.Object,
            "connection",
            parsed,
            policy,
            allowedTables,
            approvalContext,
            TestContext.Current.CancellationToken);

        Assert.Equal(DmlOperation.Insert, session.Plan.Operation);
        Assert.Equal(DmlApprovalMode.InsertValues, session.Plan.ApprovalMode);
        Assert.Equal(dialect, session.Plan.MutationCommand.TargetProvider);
        Assert.Equal(SqlStatementKind.Insert, session.Plan.MutationCommand.Kind);
        Assert.Equal(expectedRows, session.Plan.InsertRows.Length);
        Assert.Equal(expectedRows, session.Preview.AffectedRows);
        Assert.Equal(expectedRows, session.Preview.Rows.Length);
        Assert.False(
            string.IsNullOrWhiteSpace(session.Plan.PlanFingerprint),
            scenario);
        Assert.Equal(
            session.Plan.PlanFingerprint,
            session.Preview.Challenge.PlanFingerprint);
        Assert.Null(session.Preview.Challenge.RowSetFingerprint);
        Assert.Equal(fixture.ServerVersion, session.VerifiedServerVersionIdentity);
        Assert.Equal(1, fixture.Connections.CreateCount);

        Assert.Contains(
            "INSERT INTO",
            session.Plan.MutationCommand.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            renderedTableMarker,
            session.Plan.MutationCommand.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "Alice",
            session.Plan.MutationCommand.Sql,
            StringComparison.Ordinal);
        Assert.Contains(
            session.Plan.MutationCommand.Parameters,
            parameter => Equals(parameter.Value, "Alice"));
        Assert.Contains(
            session.Preview.Rows,
            row => row.Values.Any(value => Equals(value, "Alice")));

        if (expectedRows == 2)
        {
            Assert.Contains(
                session.Plan.MutationCommand.Parameters,
                parameter => Equals(parameter.Value, "Bob"));
            Assert.Contains(
                session.Preview.Rows,
                row => row.Values.Any(value => Equals(value, "Bob")));
        }

        fixture.Metadata.VerifyAll();
    }

    [Theory]
    [MemberData(nameof(SixDialectInsertSelectFailClosedMatrix))]
    public async Task TypedDmlRuntime_InsertSelectRemainsFailClosedBeforeProviderAccess(
        SqlAgentToolType dialect,
        string sql)
    {
        var provider = new Mock<ISqlProvider>(MockBehavior.Strict);
        var parsed = CoreSqlTextParser.ParseDml(sql, dialect);
        var approvalContext = new DmlApprovalExecutionContext(
            "syntax-matrix-principal",
            "syntax-matrix-target",
            dialect,
            "syntax-matrix-db");

        var error = await Assert.ThrowsAsync<NotSupportedException>(() =>
            new TypedDmlRuntime().PreviewAsync(
                provider.Object,
                "connection",
                parsed,
                SyntaxBoundaryTestSupport.Policy(),
                null,
                approvalContext,
                TestContext.Current.CancellationToken));

        Assert.Contains(
            "INSERT",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "SELECT",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        provider.VerifyNoOtherCalls();
    }

    private static object[] Case(
        SqlAgentToolType dialect,
        string scenario,
        string sql,
        int expectedRows,
        string renderedTableMarker) =>
        [dialect, scenario, sql, expectedRows, renderedTableMarker];
}
