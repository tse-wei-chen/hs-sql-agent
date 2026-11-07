using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Admin.Service.Models;
using HsSqlAgent.Server.Services;
using SqlAgent.Service.Core.Execution;
using HsSqlAgent.Approvals;
using HsSqlAgent.Provider.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

public sealed partial class CustomToolPhysicalAuditIntegrationTests
{
    [Theory]
    [InlineData(true, "dml.commit_outcome_unknown", "DmlCommitOutcomeUnknown", 2L)]
    [InlineData(false, "dml.rollback_outcome_unknown", "DmlRollbackOutcomeUnknown", 1L)]
    public async Task PhysicalLostAcknowledgement_RemainsNonRetryableAndAuditable(
        bool loseCommitAcknowledgement, string code, string auditCategory, long physicalRowCount)
    {
        await using var fixture = await Fixture.CreateAsync();
        var provider = new AcknowledgementDroppingSqliteProvider();
        var approval = new CallbackApprovalProvider(async () =>
        {
            if (!loseCommitAcknowledgement)
            {
                // Preview approved an empty target. A concurrent insert now
                // makes statement two fail after statement one has executed.
                // The transaction must roll back its first real write.
                await using var conn = new SqliteConnection(fixture.TargetConnectionString);
                await conn.OpenAsync(TestContext.Current.CancellationToken);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO items(id,note) VALUES(101,'racing-row')";
                await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            // Only the physical COMMIT phase uses the decorator. Preview,
            // profile verification, and the approval decision use real SQLite.
            provider.Mode = loseCommitAcknowledgement
                ? LostAcknowledgementMode.Commit
                : LostAcknowledgementMode.Rollback;
        });
        var proxy = fixture.CreateProxy(approval, targetProvider: provider);

        var response = await proxy.Execute(
            JsonSerializer.SerializeToElement(new { }),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(approval.LastRequest);
        Assert.Contains("status=outcome_unknown", response, StringComparison.Ordinal);
        Assert.Contains("retryable=false", response, StringComparison.Ordinal);
        Assert.Contains("errorCode=" + code, response, StringComparison.Ordinal);
        Assert.Contains("approvalRequestId=" + approval.LastRequest.RequestId,
            response, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive_note", response, StringComparison.Ordinal);
        Assert.DoesNotContain("second_value", response, StringComparison.Ordinal);
        Assert.Equal(physicalRowCount, await fixture.CountRowsAsync());

        await using var persisted = fixture.OpenAuditContext();
        var audit = await persisted.AuditLogs.AsNoTracking().SingleAsync(
            x => x.Action == "mcp.archive_order.executed",
            TestContext.Current.CancellationToken);
        Assert.Equal("outcome_unknown", audit.Result);
        Assert.Equal(auditCategory, audit.ErrorCategory);
        Assert.Equal("approved", audit.ApprovalStatus);
        Assert.Null(audit.AffectedRows);
        Assert.Equal("incoming-mcp-request", audit.RequestId);
        using var definition = JsonDocument.Parse(Assert.IsType<string>(audit.Definition));
        Assert.Equal(approval.LastRequest.RequestId,
            definition.RootElement.GetProperty("ApprovalRequestId").GetString());
        Assert.DoesNotContain("sensitive_note", audit.Definition, StringComparison.Ordinal);

        var query = await new Admin.Service.Services.AuditService(
            persisted, Moq.Mock.Of<Microsoft.AspNetCore.Http.IHttpContextAccessor>())
            .QueryAsync(new AuditLogFilter
            {
                Action = "mcp.archive_order.executed",
                Keyword = approval.LastRequest.RequestId
            }, TestContext.Current.CancellationToken);
        Assert.Single(query.Items);
        Assert.Equal(audit.EventId, query.Items.Single().EventId);
    }


    [Fact]
    public async Task LostCommitAcknowledgement_OneTimeChallengeCannotReplayAfterPhysicalCommit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var provider = new AcknowledgementDroppingSqliteProvider();
        var runtime = new TypedDmlRuntime();
        const string sql =
            "INSERT INTO main.items (id, note) VALUES (100, 'sensitive_note');" +
            "INSERT INTO main.items (id, note) VALUES (101, 'second_value')";
        var parsed = await runtime.ParseDmlBatchWithVerifiedRuntimeProfileAsync(
            provider, fixture.TargetConnectionString, sql, SqlAgentToolType.Sqlite,
            TestContext.Current.CancellationToken);
        var policy = new SecurityPolicyModel { DmlMaxAffectedRows = 16 };
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "main.items" };
        var actor = new DmlApprovalExecutionContext(
            "mcp-key:7", "db-management:42", SqlAgentToolType.Sqlite, "main");
        var session = await runtime.PreviewTransactionAsync(
            provider, fixture.TargetConnectionString, parsed, policy, tables, actor,
            TestContext.Current.CancellationToken);

        provider.Mode = LostAcknowledgementMode.Commit;
        var unknown = await Assert.ThrowsAsync<DmlCommitOutcomeUnknownException>(
            () => runtime.CommitTransactionAsync(
                provider, fixture.TargetConnectionString, session, policy, tables, actor,
                TestContext.Current.CancellationToken));
        Assert.IsType<IOException>(unknown.InnerException);
        Assert.Equal(2L, await fixture.CountRowsAsync());

        // A transport retry with the identical previously-approved session
        // is always denied, even though native SQLite already committed.
        provider.Mode = LostAcknowledgementMode.None;
        var replay = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.CommitTransactionAsync(
                provider, fixture.TargetConnectionString, session, policy, tables, actor,
                TestContext.Current.CancellationToken));
        Assert.Contains("consumed", replay.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2L, await fixture.CountRowsAsync());
    }

    private sealed class CallbackApprovalProvider(Func<Task> beforeApproval) : IDmlApprovalProvider
    {
        public DmlApprovalRequest? LastRequest { get; private set; }

        public async ValueTask<DmlApprovalResult> RequestApprovalAsync(
            DmlApprovalRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            await beforeApproval();
            return DmlApprovalResult.Approve(request, "physical-acknowledgement-test");
        }
    }

    private enum LostAcknowledgementMode { None, Commit, Rollback }

    // This is NOT an in-memory/mock transaction. Every command and COMMIT/
    // ROLLBACK is forwarded to a real file-backed Microsoft.Data.Sqlite DB.
    // Only the acknowledgement after successful physical commit/rollback is
    // discarded to simulate a transport failure.
    private sealed class AcknowledgementDroppingSqliteProvider : SqliteProvider
    {
        public LostAcknowledgementMode Mode { get; set; }

        public override DbConnection CreateConnection(string? connectionString) =>
            Mode == LostAcknowledgementMode.None
                ? base.CreateConnection(connectionString)
                : new DroppedAckConnection(new SqliteConnection(connectionString), Mode);
    }

    private sealed class DroppedAckConnection(
        SqliteConnection inner, LostAcknowledgementMode mode) : DbConnection
    {
        [AllowNull]
        public override string ConnectionString
        {
            get => inner.ConnectionString;
            set => inner.ConnectionString = value ?? string.Empty;
        }
        internal SqliteConnection Inner => inner;
        public override string Database => inner.Database;
        public override string DataSource => inner.DataSource;
        public override string ServerVersion => inner.ServerVersion;
        public override ConnectionState State => inner.State;
        public override int ConnectionTimeout => inner.ConnectionTimeout;
        public override void Open() => inner.Open();
        public override Task OpenAsync(CancellationToken cancellationToken) =>
            inner.OpenAsync(cancellationToken);
        public override void Close() => inner.Close();
        public override void ChangeDatabase(string databaseName) => inner.ChangeDatabase(databaseName);

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            new DroppedAckTransaction(this, (SqliteTransaction)inner.BeginTransaction(isolationLevel), mode);

        protected override DbCommand CreateDbCommand() =>
            new DroppedAckCommand(this, inner.CreateCommand());

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class DroppedAckTransaction(
        DroppedAckConnection owner, SqliteTransaction inner, LostAcknowledgementMode mode)
        : DbTransaction
    {
        internal SqliteTransaction Inner => inner;
        public override IsolationLevel IsolationLevel => inner.IsolationLevel;
        protected override DbConnection DbConnection => owner;
        public override void Commit()
        {
            inner.Commit();
            if (mode == LostAcknowledgementMode.Commit)
                throw new IOException("Simulated lost COMMIT acknowledgement after SQLite committed.");
        }
        public override async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            await inner.CommitAsync(cancellationToken);
            if (mode == LostAcknowledgementMode.Commit)
                throw new IOException("Simulated lost COMMIT acknowledgement after SQLite committed.");
        }
        public override void Rollback()
        {
            inner.Rollback();
            if (mode == LostAcknowledgementMode.Rollback)
                throw new IOException("Simulated lost ROLLBACK acknowledgement after SQLite rolled back.");
        }
        public override async Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            await inner.RollbackAsync(cancellationToken);
            if (mode == LostAcknowledgementMode.Rollback)
                throw new IOException("Simulated lost ROLLBACK acknowledgement after SQLite rolled back.");
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class DroppedAckCommand(DroppedAckConnection owner, SqliteCommand inner) : DbCommand
    {
        [AllowNull]
        public override string CommandText
        {
            get => inner.CommandText;
            set => inner.CommandText = value ?? string.Empty;
        }
        public override int CommandTimeout
        {
            get => inner.CommandTimeout;
            set => inner.CommandTimeout = value;
        }
        public override CommandType CommandType
        {
            get => inner.CommandType;
            set => inner.CommandType = value;
        }
        public override bool DesignTimeVisible
        {
            get => inner.DesignTimeVisible;
            set => inner.DesignTimeVisible = value;
        }
        public override UpdateRowSource UpdatedRowSource
        {
            get => inner.UpdatedRowSource;
            set => inner.UpdatedRowSource = value;
        }
        protected override DbConnection? DbConnection
        {
            get => owner;
            set
            {
                if (value is DroppedAckConnection wrapped)
                    inner.Connection = wrapped.Inner;
                else
                    inner.Connection = (SqliteConnection?)value;
            }
        }
        protected override DbTransaction? DbTransaction
        {
            get => inner.Transaction;
            set => inner.Transaction = value switch
            {
                DroppedAckTransaction wrapped => wrapped.Inner,
                SqliteTransaction sqlite => sqlite,
                null => null,
                _ => throw new InvalidOperationException("Unexpected transaction provider.")
            };
        }
        protected override DbParameterCollection DbParameterCollection => inner.Parameters;
        public override void Cancel() => inner.Cancel();
        public override int ExecuteNonQuery() => inner.ExecuteNonQuery();
        public override object? ExecuteScalar() => inner.ExecuteScalar();
        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) =>
            inner.ExecuteNonQueryAsync(cancellationToken);
        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken) =>
            inner.ExecuteScalarAsync(cancellationToken);
        public override void Prepare() => inner.Prepare();
        protected override DbParameter CreateDbParameter() => inner.CreateParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
            inner.ExecuteReader(behavior);
        protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
            CommandBehavior behavior, CancellationToken cancellationToken) =>
            await inner.ExecuteReaderAsync(behavior, cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
