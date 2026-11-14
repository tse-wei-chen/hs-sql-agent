using Admin.Service.Models;
using HsSqlAgent.Server.Services;
using Microsoft.Data.Sqlite;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public sealed class DmlRowSetSyntaxBoundaryMatrixTests
{
    public static IEnumerable<object[]> SixDialectRowSetBoundaryMatrix()
    {
        foreach (var dialect in Enum.GetValues<SqlAgentToolType>())
        {
            yield return
            [
                dialect,
                "update",
                "UPDATE users SET name = 'Updated' WHERE id = 1",
                SqlStatementKind.Update,
                DmlOperation.Update,
                "UPDATE"
            ];

            yield return
            [
                dialect,
                "delete",
                "DELETE FROM users WHERE id = 2",
                SqlStatementKind.Delete,
                DmlOperation.Delete,
                "DELETE FROM"
            ];
        }
    }

    [Fact]
    public void SixDialectRowSetBoundaryMatrix_HasStableCoverage()
    {
        var cases = SixDialectRowSetBoundaryMatrix().ToArray();

        Assert.Equal(12, cases.Length);
        foreach (var dialect in Enum.GetValues<SqlAgentToolType>())
        {
            Assert.Equal(
                2,
                cases.Count(item => Equals(item[0], dialect)));
        }
    }

    [Theory]
    [MemberData(nameof(SixDialectRowSetBoundaryMatrix))]
    public async Task TypedDmlRuntime_PreviewsUpdateDeleteThroughRealRowSetApprovalPath(
        SqlAgentToolType dialect,
        string scenario,
        string sql,
        SqlStatementKind expectedKind,
        DmlOperation expectedOperation,
        string mutationMarker)
    {
        var connectionString =
            $"Data Source=dml-boundary-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync(TestContext.Current.CancellationToken);
        await using (var setup = anchor.CreateCommand())
        {
            setup.CommandText =
                "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL);" +
                "INSERT INTO users (id, name) VALUES (1, 'Alice'), (2, 'Bob');";
            await setup.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken);
        }

        var fixture = SyntaxBoundaryTestSupport.DmlRowSetProvider(dialect);
        var parsed = CoreSqlTextParser.ParseDml(sql, dialect);
        var policy = SyntaxBoundaryTestSupport.Policy();
        policy.DmlMaxAffectedRows = 0;
        IReadOnlySet<string> allowedTables =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                fixture.QualifiedTable
            };
        var approvalContext = new DmlApprovalExecutionContext(
            "syntax-rowset-principal",
            "syntax-rowset-target",
            dialect,
            "syntax-rowset-db");
        var runtime = new TypedDmlRuntime(
            previewTransactionFactory:
                new SqliteBoundaryPreviewTransactionFactory());

        var session = await runtime.PreviewAsync(
            fixture.Provider.Object,
            connectionString,
            parsed,
            policy,
            allowedTables,
            approvalContext,
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedOperation, session.Plan.Operation);
        Assert.Equal(DmlApprovalMode.RowSetMutation, session.Plan.ApprovalMode);
        Assert.Equal(expectedKind, session.Plan.MutationCommand.Kind);
        Assert.Equal(dialect, session.Plan.MutationCommand.TargetProvider);
        Assert.False(
            string.IsNullOrWhiteSpace(session.Plan.PlanFingerprint),
            scenario);

        var match = Assert.IsType<CompiledSqlCommand>(
            session.Plan.MatchQueryCommand);
        Assert.Equal(SqlStatementKind.Select, match.Kind);
        Assert.Equal(dialect, match.TargetProvider);
        Assert.Single(session.Plan.RowIdentityColumns);
        Assert.Equal(
            "id",
            session.Plan.RowIdentityColumns[0],
            ignoreCase: true);

        Assert.Equal(1, session.Preview.AffectedRows);
        Assert.Single(session.Preview.Rows);
        Assert.NotNull(session.Preview.Challenge.RowSetFingerprint);
        Assert.False(
            string.IsNullOrWhiteSpace(
                session.Preview.Challenge.RowSetFingerprint));
        Assert.Equal(
            session.Plan.PlanFingerprint,
            session.Preview.Challenge.PlanFingerprint);

        Assert.Contains(
            mutationMarker,
            session.Plan.MutationCommand.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "users",
            session.Plan.MutationCommand.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "SELECT",
            match.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "users",
            match.Sql,
            StringComparison.OrdinalIgnoreCase);

        if (expectedOperation == DmlOperation.Update)
        {
            Assert.DoesNotContain(
                "Updated",
                session.Plan.MutationCommand.Sql,
                StringComparison.Ordinal);
            Assert.Contains(
                session.Plan.MutationCommand.Parameters,
                parameter => Equals(parameter.Value, "Updated"));
            Assert.Contains(
                session.Preview.Rows,
                row => row.Values.Any(value => Convert.ToInt64(value) == 1L));
        }
        else
        {
            Assert.Contains(
                session.Preview.Rows,
                row => row.Values.Any(value => Convert.ToInt64(value) == 2L));
        }

        Assert.Equal(2, fixture.Connections.CreateCount);
        fixture.Metadata.VerifyAll();
    }
}
