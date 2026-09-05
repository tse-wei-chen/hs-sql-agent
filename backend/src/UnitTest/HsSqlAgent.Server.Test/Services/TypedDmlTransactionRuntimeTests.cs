using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using Admin.Service.Models;
using HsSqlAgent.Server.Services;
using HsSqlAgent.SqlCore.SqlParsing;
using Moq;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public sealed class TypedDmlTransactionRuntimeTests
{
    private static readonly DmlApprovalExecutionContext ApprovalContext = new(
        "mcp-key:7",
        "db-management:11",
        SqlAgentToolType.Postgres,
        "appdb");

    [Fact]
    public async Task PreviewTransactionAsync_TotalAffectedRowsExceedsPolicy_FailsClosed()
    {
        var (provider, metadata, connections) = CreateProvider();
        var batch = CoreDmlBatchTextParser.ParseDmlBatch(
            "INSERT INTO public.users (id, name) VALUES (1, 'Alice');" +
            "INSERT INTO public.users (id, name) VALUES (2, 'Bob')",
            SqlAgentToolType.Postgres);
        var runtime = new TypedDmlRuntime();
        var policy = new SecurityPolicyModel { DmlMaxAffectedRows = 1 };

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            runtime.PreviewTransactionAsync(
                provider.Object,
                "connection",
                batch,
                policy,
                allowedTables: null,
                ApprovalContext,
                TestContext.Current.CancellationToken));

        Assert.Contains("transaction", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("affectedRows=2", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, connections.CreateCount);
        metadata.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CommitTransactionAsync_ReorderedApprovedStatements_FailsBeforeExecution()
    {
        var (provider, metadata, connections) = CreateProvider();
        var batch = CoreDmlBatchTextParser.ParseDmlBatch(
            "INSERT INTO public.users (id, name) VALUES (1, 'Alice');" +
            "INSERT INTO public.users (id, name) VALUES (2, 'Bob')",
            SqlAgentToolType.Postgres);
        var runtime = new TypedDmlRuntime();
        var policy = new SecurityPolicyModel { DmlMaxAffectedRows = 2 };
        var approved = await runtime.PreviewTransactionAsync(
            provider.Object,
            "connection",
            batch,
            policy,
            allowedTables: null,
            ApprovalContext,
            TestContext.Current.CancellationToken);
        var reordered = new TypedDmlTransactionApprovalSession(
            approved.Statements.Reverse().ToImmutableArray(),
            approved.Challenge);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.CommitTransactionAsync(
                provider.Object,
                "connection",
                reordered,
                policy,
                currentAllowedTables: null,
                ApprovalContext,
                TestContext.Current.CancellationToken));

        Assert.Contains("plan", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("order", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, connections.CreateCount);
        metadata.VerifyNoOtherCalls();
    }

    private static (
        Mock<ISqlProvider> Provider,
        Mock<IProviderMetadataReader> Metadata,
        VersionedConnectionFactory Connections) CreateProvider()
    {
        var metadata = new Mock<IProviderMetadataReader>(MockBehavior.Strict);
        var connections = new VersionedConnectionFactory("17.5 (Debian 17.5-1)");
        var provider = new Mock<ISqlProvider>(MockBehavior.Strict);
        provider.SetupGet(x => x.Metadata).Returns(metadata.Object);
        provider.SetupGet(x => x.Type).Returns(SqlAgentToolType.Postgres);
        provider.SetupGet(x => x.Connections).Returns(connections);
        return (provider, metadata, connections);
    }

    private sealed class VersionedConnectionFactory(string serverVersion) : IDbConnectionFactory
    {
        private readonly string _serverVersion = serverVersion;
        public int CreateCount { get; private set; }

        public DbConnection Create(string connectionString)
        {
            CreateCount++;
            return new VersionedConnection(_serverVersion, connectionString);
        }
    }

    private sealed class VersionedConnection(string serverVersion, string connectionString) : DbConnection
    {
        private readonly string _serverVersion = serverVersion;
        private string _connectionString = connectionString;
        private ConnectionState _state = ConnectionState.Closed;

        public override string ConnectionString
        {
            get => _connectionString;
            set => _connectionString = value ?? string.Empty;
        }

        public override string Database => "test";
        public override string DataSource => "test";
        public override string ServerVersion => _serverVersion;
        public override ConnectionState State => _state;

        public override void ChangeDatabase(string databaseName) { }
        public override void Close() => _state = ConnectionState.Closed;
        public override void Open() => _state = ConnectionState.Open;
        public override Task OpenAsync(CancellationToken cancellationToken)
        {
            _state = ConnectionState.Open;
            return Task.CompletedTask;
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            throw new NotSupportedException("Commit must fail before a transaction is opened in these tests.");

        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }
}
