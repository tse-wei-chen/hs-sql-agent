using HsSqlAgent.Server.Services;
using Moq;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public sealed class VersionGatedDmlSyntaxBoundaryTests
{
    public static IEnumerable<object[]> SupportedReturningMatrix()
    {
        yield return
        [
            SqlAgentToolType.Sqlite,
            "3.46.0",
            new Version(3, 46)
        ];
        yield return
        [
            SqlAgentToolType.Firebird,
            "5.0",
            new Version(5, 0)
        ];
    }

    public static IEnumerable<object[]> RejectedOldReturningMatrix()
    {
        yield return
        [
            SqlAgentToolType.Sqlite,
            "3.34.0",
            "3.35"
        ];
        yield return
        [
            SqlAgentToolType.Firebird,
            "4.0",
            "5.0"
        ];
    }

    [Theory]
    [MemberData(nameof(SupportedReturningMatrix))]
    public async Task NativeReturning_UsesVerifiedSourceAndTargetVersions(
        SqlAgentToolType dialect,
        string expectedIdentity,
        Version expectedVersion)
    {
        var fixture = SyntaxBoundaryTestSupport.DmlProvider(dialect);
        var runtime = new TypedDmlRuntime();
        var sql =
            "INSERT INTO users (id, name) VALUES (1, 'Alice') RETURNING id";

        var parsed = await runtime.ParseDmlWithVerifiedRuntimeProfileAsync(
            fixture.Provider.Object,
            "connection",
            sql,
            dialect,
            TestContext.Current.CancellationToken);

        var policy = SyntaxBoundaryTestSupport.Policy();
        policy.DmlMaxAffectedRows = 1;
        var session = await runtime.PreviewAsync(
            fixture.Provider.Object,
            "connection",
            parsed,
            policy,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                fixture.QualifiedTable
            },
            new DmlApprovalExecutionContext(
                "versioned-dml-principal",
                "versioned-dml-target",
                dialect,
                "versioned-dml-db"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(parsed.SourceProfile);
        Assert.Equal(expectedVersion, parsed.SourceProfile.ServerVersion);
        Assert.Equal(expectedIdentity, session.VerifiedServerVersionIdentity);
        Assert.True(session.Plan.MutationCommand.ReturnsRows);
        Assert.Contains(
            "RETURNING",
            session.Plan.MutationCommand.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Single(session.Plan.InsertRows);
        Assert.Single(session.Preview.Rows);
    }

    [Theory]
    [MemberData(nameof(RejectedOldReturningMatrix))]
    public async Task NativeReturning_OldRuntimeVersionFailsAtSourceCapabilityBoundary(
        SqlAgentToolType dialect,
        string serverVersion,
        string minimumVersion)
    {
        var connections = new BoundaryConnectionFactory(serverVersion);
        var provider = new Mock<ISqlProvider>(MockBehavior.Strict);
        provider.SetupGet(x => x.Type).Returns(dialect);
        provider.SetupGet(x => x.Connections).Returns(connections);

        var error = await Assert.ThrowsAsync<SqlParseException>(() =>
            new TypedDmlRuntime().ParseDmlWithVerifiedRuntimeProfileAsync(
                provider.Object,
                "connection",
                "INSERT INTO users (id, name) VALUES (1, 'Alice') RETURNING id",
                dialect,
                TestContext.Current.CancellationToken));

        Assert.Contains(
            minimumVersion,
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(error.Diagnostic);
        Assert.Equal(
            "SQL_SOURCE_CAPABILITY_REJECTED",
            error.Diagnostic.Code);
        Assert.Equal(
            SqlDiagnosticStage.SourceValidation,
            error.Diagnostic.Stage);
        Assert.Equal(
            SqlDiagnosticCategory.Capability,
            error.Diagnostic.Category);
        Assert.Equal(1, connections.CreateCount);
        provider.VerifyAll();
    }
}
