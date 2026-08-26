using System.Collections.Immutable;
using System.Data.Common;
using Admin.Service.Models;
using HsSqlAgent.Server.Services;
using Moq;
using HsSqlAgent.SqlCore.Core.Compilation;
using SqlAgent.Service.Core.Execution;
using SqlAgent.Service.Core.Providers;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public class TypedDmlRuntimeTests
{
    [Fact]
    public async Task PreviewAsync_InsertSelectFailsClosedBeforeProviderAccess()
    {
        var provider = new Mock<ISqlProvider>(MockBehavior.Strict);
        var runtime = new TypedDmlRuntime();
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO public.users (name) SELECT name FROM public.pending_users",
            SqlAgentToolType.Postgres);

        var error = await Assert.ThrowsAsync<NotSupportedException>(() =>
            runtime.PreviewAsync(
                provider.Object,
                "connection",
                parsed,
                new SecurityPolicyModel(),
                null,
                TestContext.Current.CancellationToken));

        Assert.Contains("INSERT", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SELECT", error.Message, StringComparison.OrdinalIgnoreCase);
        provider.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PreviewAsync_InsertValuesBuildsExactPayloadApproval()
    {
        var metadata = new Mock<IProviderMetadataReader>(MockBehavior.Strict);
        metadata
            .Setup(x => x.GetColumnsAsync(
                "connection",
                "public",
                "users",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new DatabaseColumnMetadata("public", "users", "id", "integer", false),
                new DatabaseColumnMetadata("public", "users", "name", "text", false)
            ]);
        var connections = new ThrowingConnectionFactory();
        var provider = new Mock<ISqlProvider>(MockBehavior.Strict);
        provider.SetupGet(x => x.Metadata).Returns(metadata.Object);
        provider.SetupGet(x => x.Type).Returns(SqlAgentToolType.Postgres);
        provider.SetupGet(x => x.Connections).Returns(connections);

        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO public.users (id, name) VALUES (1, 'Alice'), (2, 'Bob')",
            SqlAgentToolType.Postgres);
        IReadOnlySet<string> allowedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "public.users"
        };
        var policy = new SecurityPolicyModel { DmlMaxAffectedRows = 2 };

        var session = await new TypedDmlRuntime().PreviewAsync(
            provider.Object,
            "connection",
            parsed,
            policy,
            allowedTables,
            TestContext.Current.CancellationToken);

        Assert.Equal(DmlApprovalMode.InsertValues, session.Plan.ApprovalMode);
        Assert.Equal(DmlOperation.Insert, session.Plan.Operation);
        Assert.Equal("public.users", session.Plan.TableName);
        Assert.Equal(2, session.Plan.MaxAffectedRows);
        Assert.Null(session.Plan.MatchQueryCommand);
        Assert.Empty(session.Plan.RowIdentityColumns);
        Assert.Equal(2, session.Plan.InsertRows.Length);
        Assert.Equal(2, session.Preview.AffectedRows);
        Assert.Equal(2, session.Preview.Rows.Length);
        Assert.Equal("Alice", session.Preview.Rows[0]["name"]);
        Assert.Null(session.Preview.Challenge.RowSetFingerprint);
        Assert.Equal(session.Plan.PlanFingerprint, session.Preview.Challenge.PlanFingerprint);
        Assert.Equal(
            TypedDmlRuntime.ComputePolicyVersion(policy, allowedTables),
            session.Plan.PolicyVersion);
        Assert.Equal(0, connections.CreateCount);
        metadata.VerifyAll();
    }

    [Fact]
    public async Task CommitAsync_RejectsPolicyChangeBeforeProviderAccess()
    {
        var previewPolicy = new SecurityPolicyModel
        {
            RequireWhereForUpdate = true,
            DmlMaxAffectedRows = 25
        };
        var session = BuildSession(
            TypedDmlRuntime.ComputePolicyVersion(previewPolicy, null));
        var changedPolicy = previewPolicy.Clone();
        changedPolicy.DmlMaxAffectedRows = 10;
        var provider = new Mock<ISqlProvider>(MockBehavior.Strict);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new TypedDmlRuntime().CommitAsync(
                provider.Object,
                "connection",
                session,
                changedPolicy,
                null,
                TestContext.Current.CancellationToken));

        Assert.Contains("security policy", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("new preview", error.Message, StringComparison.OrdinalIgnoreCase);
        provider.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CommitAsync_RejectsWhitelistChangeBeforeProviderAccess()
    {
        var policy = new SecurityPolicyModel { DmlMaxAffectedRows = 25 };
        IReadOnlySet<string> previewTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "public.users"
        };
        var session = BuildSession(
            TypedDmlRuntime.ComputePolicyVersion(policy, previewTables));
        IReadOnlySet<string> changedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "public.users",
            "public.orders"
        };
        var provider = new Mock<ISqlProvider>(MockBehavior.Strict);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new TypedDmlRuntime().CommitAsync(
                provider.Object,
                "connection",
                session,
                policy,
                changedTables,
                TestContext.Current.CancellationToken));

        Assert.Contains("table authorization", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("new preview", error.Message, StringComparison.OrdinalIgnoreCase);
        provider.VerifyNoOtherCalls();
    }

    [Fact]
    public void CommitApi_DoesNotExposeSecurityContextFreeOverload()
    {
        var overloads = typeof(TypedDmlRuntime)
            .GetMethods()
            .Where(method => method.Name == nameof(TypedDmlRuntime.CommitAsync))
            .ToArray();

        var commit = Assert.Single(overloads);
        var parameters = commit.GetParameters();
        Assert.Contains(parameters, parameter => parameter.ParameterType == typeof(SecurityPolicyModel));
        Assert.Contains(parameters, parameter => parameter.Name == "currentAllowedTables");
    }

    [Fact]
    public void ComputePolicyVersion_IsStableAcrossWhitelistOrder()
    {
        var policy = new SecurityPolicyModel
        {
            RequireWhereForUpdate = true,
            RequireWhereForDelete = true,
            AllowFullTableUpdate = false,
            AllowFullTableDelete = false,
            DmlMaxAffectedRows = 25,
            UpdatedAt = new DateTime(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc)
        };

        var left = TypedDmlRuntime.ComputePolicyVersion(
            policy,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "public.users",
                "public.orders"
            });
        var right = TypedDmlRuntime.ComputePolicyVersion(
            policy,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "public.orders",
                "public.users"
            });

        Assert.Equal(left, right);
    }

    [Fact]
    public void ComputePolicyVersion_ChangesWhenDmlPolicyChanges()
    {
        var original = new SecurityPolicyModel
        {
            RequireWhereForUpdate = true,
            RequireWhereForDelete = true,
            DmlMaxAffectedRows = 25
        };
        var changed = original.Clone();
        changed.DmlMaxAffectedRows = 26;

        var before = TypedDmlRuntime.ComputePolicyVersion(original, null);
        var after = TypedDmlRuntime.ComputePolicyVersion(changed, null);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void ComputePolicyVersion_ChangesWhenWhitelistChanges()
    {
        var policy = new SecurityPolicyModel { DmlMaxAffectedRows = 25 };

        var before = TypedDmlRuntime.ComputePolicyVersion(
            policy,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "public.users" });
        var after = TypedDmlRuntime.ComputePolicyVersion(
            policy,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "public.users",
                "public.orders"
            });

        Assert.NotEqual(before, after);
    }

    private static TypedDmlApprovalSession BuildSession(string policyVersion)
    {
        var command = new CompiledSqlCommand(
            "UPDATE public.users SET status = @p0 WHERE id = @p1",
            ImmutableArray<SqlParameterValue>.Empty,
            SqlStatementKind.Update,
            "command-fingerprint",
            SqlAgentToolType.Postgres);
        var match = new CompiledSqlCommand(
            "SELECT id FROM public.users WHERE id = @p0",
            ImmutableArray<SqlParameterValue>.Empty,
            SqlStatementKind.Select,
            "match-fingerprint",
            SqlAgentToolType.Postgres);
        var plan = new ValidatedDmlPlan(
            DmlOperation.Update,
            "public.users",
            command,
            match,
            ImmutableArray.Create("id"),
            DmlRowIdentityAssurance.Strict,
            "plan-fingerprint",
            policyVersion,
            TimeSpan.FromMinutes(5),
            25);
        var now = DateTimeOffset.UtcNow;
        var challenge = new DmlApprovalChallenge(
            "plan-fingerprint",
            "rowset-fingerprint",
            1,
            policyVersion,
            now,
            now.AddMinutes(5),
            "nonce");

        return new TypedDmlApprovalSession(
            plan,
            new DmlPreview(
                DmlOperation.Update,
                "public.users",
                1,
                ImmutableArray<IReadOnlyDictionary<string, object?>>.Empty,
                challenge));
    }

    private sealed class ThrowingConnectionFactory : IDbConnectionFactory
    {
        public int CreateCount { get; private set; }

        public DbConnection Create(string connectionString)
        {
            CreateCount++;
            throw new InvalidOperationException("INSERT VALUES preview must not open a database connection.");
        }
    }
}
