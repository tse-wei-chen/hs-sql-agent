using System.Data;
using System.Data.Common;
using Admin.Service.Models;
using HsSqlAgent.Provider.Abstractions;
using HsSqlAgent.Server.Services;
using HsSqlAgent.SqlCore.Models;
using Moq;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public class TypedQueryRuntimeTests
{
    [Fact]
    public void SqlStrategyContract_DoesNotExposeExecutionMethods()
    {
        var methodNames = typeof(ISqlStrategy).GetMethods().Select(method => method.Name).ToArray();
        Assert.DoesNotContain("ExecuteQueryAsync", methodNames);
        Assert.DoesNotContain("ExecuteDmlAsync", methodNames);
    }

    [Fact]
    public void Compile_AppliesCoreMaxRowsAndKeepsValuesParameterized()
    {
        var runtime = new TypedQueryRuntime();
        var provider = CreateProvider(SqlAgentToolType.Postgres);
        var command = runtime.Compile(
            provider.Object,
            "SELECT id FROM public.users WHERE status = 'active'",
            SqlAgentToolType.Postgres,
            CreatePolicy(maxRows: 25),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.users" });

        Assert.Equal(SqlStatementKind.Select, command.Kind);
        Assert.Contains("LIMIT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("active", command.Sql, StringComparison.Ordinal);
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, "active"));
        Assert.False(string.IsNullOrWhiteSpace(command.PlanFingerprint));
    }

    [Fact]
    public void Compile_RejectsUnauthorizedTableBeforeExecution()
    {
        var runtime = new TypedQueryRuntime();
        var provider = CreateProvider(SqlAgentToolType.Postgres);
        Assert.Throws<UnauthorizedAccessException>(() => runtime.Compile(
            provider.Object,
            "SELECT id FROM public.secrets",
            SqlAgentToolType.Postgres,
            CreatePolicy(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.users" }));
    }

    [Fact]
    public void Compile_PolicyOrAuthorizationChange_ChangesPlanFingerprint()
    {
        var runtime = new TypedQueryRuntime();
        var provider = CreateProvider(SqlAgentToolType.Postgres);
        const string sql = "SELECT id FROM public.users";
        var first = runtime.Compile(
            provider.Object, sql, SqlAgentToolType.Postgres, CreatePolicy(maxRows: 10),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.users" });
        var second = runtime.Compile(
            provider.Object, sql, SqlAgentToolType.Postgres, CreatePolicy(maxRows: 20),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.users" });
        Assert.NotEqual(first.PlanFingerprint, second.PlanFingerprint);
    }

    [Fact]
    public void Compile_WithVerifiedNativeProfile_UsesItAsSourceCapabilityProof()
    {
        var runtime = new TypedQueryRuntime();
        var provider = CreateProvider(SqlAgentToolType.MySQL);
        var verifiedProfile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.MySQL,
            new Version(8, 0, 36));

        var command = runtime.Compile(
            provider.Object,
            "WITH RECURSIVE x(n) AS (" +
            "SELECT 1 UNION ALL SELECT n + 1 FROM x WHERE n < 3" +
            ") SELECT n FROM x",
            SqlAgentToolType.MySQL,
            CreatePolicy(),
            allowedTables: null,
            verifiedProfile);

        Assert.Equal(SqlAgentToolType.MySQL, command.TargetProvider);
        Assert.Equal(SqlStatementKind.Select, command.Kind);
        Assert.Contains(
            "WITH RECURSIVE",
            command.Sql,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_WithVerifiedTargetProfile_DoesNotTreatItAsSourceProfile()
    {
        var runtime = new TypedQueryRuntime();
        var provider = CreateProvider(SqlAgentToolType.MsSqlServer);
        var targetProfile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.MsSqlServer,
            new Version(16, 0));

        var command = runtime.Compile(
            provider.Object,
            "SELECT id FROM public.users",
            SqlAgentToolType.Postgres,
            CreatePolicy(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.users" },
            targetProfile);

        Assert.Equal(SqlAgentToolType.MsSqlServer, command.TargetProvider);
        Assert.Equal(SqlStatementKind.Select, command.Kind);
    }

    [Fact]
    public void CompileWithFacts_UsesSameBoundDocumentForAuditFactsAndCommand()
    {
        var runtime = new TypedQueryRuntime();
        var provider = CreateProvider(SqlAgentToolType.Postgres);
        const string sql =
            "WITH active AS (SELECT id FROM public.users WHERE enabled = true) " +
            "SELECT a.id FROM active a WHERE EXISTS (SELECT 1 FROM public.orders o WHERE o.user_id = a.id)";

        var result = runtime.CompileWithFacts(
            provider.Object,
            sql,
            SqlAgentToolType.Postgres,
            CreatePolicy(maxRows: 25),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.users", "public.orders" },
            targetProfile: null);

        Assert.Equal(SqlStatementKind.Select, result.Command.Kind);
        Assert.Contains("public.users", result.Facts.ReferencedTables, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("public.orders", result.Facts.ReferencedTables, StringComparer.OrdinalIgnoreCase);
        Assert.True(result.Facts.ContainsCte);
        Assert.True(result.Facts.ContainsSubquery);
        Assert.Contains("LIMIT", result.Command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateVerifiedTargetProfile_UsesOpenConnectionServerVersion()
    {
        var connection = new Mock<DbConnection>();
        connection.SetupGet(x => x.State).Returns(ConnectionState.Open);
        connection.SetupGet(x => x.ServerVersion).Returns("17.5 (Debian 17.5-1)");
        var profile = TypedQueryRuntime.CreateVerifiedTargetProfile(SqlAgentToolType.Postgres, connection.Object);
        Assert.Equal(SqlAgentToolType.Postgres, profile.Provider);
        Assert.Equal(new Version(17, 5), profile.ServerVersion);
    }

    [Fact]
    public void CreateVerifiedTargetProfile_RequiresOpenConnection()
    {
        var connection = new Mock<DbConnection>();
        connection.SetupGet(x => x.State).Returns(ConnectionState.Closed);
        Assert.Throws<InvalidOperationException>(() =>
            TypedQueryRuntime.CreateVerifiedTargetProfile(SqlAgentToolType.Postgres, connection.Object));
    }


    [Fact]
    public async Task ExecuteAsync_CompilerAuthorizationFailure_IsNotMappedAsProviderError()
    {
        var connection = CreateOpenConnection();
        var connections = new Mock<IDbConnectionFactory>();
        connections.Setup(x => x.Create("connection")).Returns(connection.Object);

        var provider = new Mock<ISqlProvider>(MockBehavior.Strict);
        provider.SetupGet(x => x.Type).Returns(SqlAgentToolType.Postgres);
        provider.SetupGet(x => x.Connections).Returns(connections.Object);

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new TypedQueryRuntime().ExecuteAsync(
                provider.Object,
                "connection",
                "SELECT id FROM public.secrets",
                SqlAgentToolType.Postgres,
                CreatePolicy(),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.users" },
                TestContext.Current.CancellationToken));

        Assert.Contains("not authorized", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_DbOpenFailure_UsesProviderErrorMapper()
    {
        var dbError = new TestDbException("database unavailable");
        var mapped = new InvalidOperationException("mapped provider failure", dbError);
        var connection = new Mock<DbConnection>();
        connection.Setup(x => x.OpenAsync(It.IsAny<CancellationToken>())).ThrowsAsync(dbError);
        var connections = new Mock<IDbConnectionFactory>();
        connections.Setup(x => x.Create("connection")).Returns(connection.Object);
        var errors = new Mock<IProviderErrorMapper>();
        errors.Setup(x => x.Map(dbError, "query")).Returns(mapped);

        var provider = new Mock<ISqlProvider>();
        provider.SetupGet(x => x.Connections).Returns(connections.Object);
        provider.SetupGet(x => x.Errors).Returns(errors.Object);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new TypedQueryRuntime().ExecuteAsync(
                provider.Object,
                "connection",
                "SELECT 1",
                SqlAgentToolType.Postgres,
                CreatePolicy(),
                null,
                TestContext.Current.CancellationToken));

        Assert.Same(mapped, actual);
        errors.Verify(x => x.Map(dbError, "query"), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_Cancellation_DoesNotUseProviderErrorMapper()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        var connection = new Mock<DbConnection>();
        connection.Setup(x => x.OpenAsync(source.Token))
            .ThrowsAsync(new OperationCanceledException(source.Token));
        var connections = new Mock<IDbConnectionFactory>();
        connections.Setup(x => x.Create("connection")).Returns(connection.Object);
        var errors = new Mock<IProviderErrorMapper>(MockBehavior.Strict);

        var provider = new Mock<ISqlProvider>();
        provider.SetupGet(x => x.Connections).Returns(connections.Object);
        provider.SetupGet(x => x.Errors).Returns(errors.Object);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new TypedQueryRuntime().ExecuteAsync(
                provider.Object,
                "connection",
                "SELECT 1",
                SqlAgentToolType.Postgres,
                CreatePolicy(),
                null,
                source.Token));

        errors.VerifyNoOtherCalls();
    }

    private static Mock<DbConnection> CreateOpenConnection()
    {
        var connection = new Mock<DbConnection>();
        connection.Setup(x => x.OpenAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        connection.SetupGet(x => x.State).Returns(ConnectionState.Open);
        connection.SetupGet(x => x.ServerVersion).Returns("17.0");
        return connection;
    }

    private sealed class TestDbException(string message) : DbException(message)
    {
    }

    private static Mock<ISqlProvider> CreateProvider(SqlAgentToolType type)
    {
        var provider = new Mock<ISqlProvider>();
        provider.SetupGet(x => x.Type).Returns(type);
        return provider;
    }

    private static SecurityPolicyModel CreatePolicy(int maxRows = 100) => new()
    {
        QueryMaxRows = maxRows,
        QueryTimeoutSeconds = 30,
        RequireWhereForUpdate = true,
        RequireWhereForDelete = true,
        AllowFullTableUpdate = false,
        AllowFullTableDelete = false,
        DmlMaxAffectedRows = 100
    };
}
