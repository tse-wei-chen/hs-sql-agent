using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Admin.Service.Models;
using HsSqlAgent.Provider.SqlServer;
using HsSqlAgent.Server.Services;
using Microsoft.Data.SqlClient;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

/// <summary>
/// #575: real SQL Server 16 transactions; simulated ACK is dropped only
/// *after* native Sql CommitAsync or RollbackAsync finishes.
/// </summary>
public sealed partial class SqlServerPhysicalUnknownOutcomeIntegrationTests
{
    private static readonly SecurityPolicyModel Policy = new()
    {
        DmlMaxAffectedRows = 16,
        RequireWhereForUpdate = true,
        RequireWhereForDelete = true
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LostSqlServerTransactionAck_PhysicalOutcomeAndConsumedApproval(
        bool dropCommitAcknowledgement)
    {
        var source = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(source))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Real SQL Server 16 lost ACK proof requires TEST_POSTGRES_CONNECTION.");
            Assert.Skip("Requires a real SQL Server 16 service.");
        }

        var token = TestContext.Current.CancellationToken;
        var connectionString = new SqlConnectionStringBuilder(source)
        {
            Pooling = false
        }.ConnectionString;
        var table = "dmlack_" + Guid.NewGuid().ToString("N")[..15];
        await using var independent = new SqlConnection(connectionString);
        for (var retry = 0; ; retry++)
        {
            try { await independent.OpenAsync(token); break; }
            catch (SqlException) when (retry < 24 && !token.IsCancellationRequested)
            { await Task.Delay(TimeSpan.FromSeconds(3), token); }
        }
        try
        {
            await ExecAsync(independent,
                "CREATE TABLE " + "dbo." + table + "(id INTEGER PRIMARY KEY,note NVARCHAR(64) NOT NULL)", token);
            var provider = new AcknowledgementDroppingMsSqlServerProvider();
            var snapshot = new NativeDmlSchemaSnapshotService();
            var runtime = new TypedDmlRuntime(
                schemaSnapshotSource: snapshot, schemaFreshnessVerifier: snapshot);
            var actor = new DmlApprovalExecutionContext(
                "mcp-key:7", "db-management:42", SqlAgentToolType.MsSqlServer,
                independent.Database);
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dbo." + table + "" };
            var sql = "INSERT INTO " + "dbo." + table + "(id,note) VALUES(100,'sensitive_note');"
                      + "INSERT INTO " + "dbo." + table + "(id,note) VALUES(101,'second_value')";
            var parsed = await runtime.ParseDmlBatchWithVerifiedRuntimeProfileAsync(
                provider, connectionString, sql, SqlAgentToolType.MsSqlServer, token);
            var session = await runtime.PreviewTransactionAsync(
                provider, connectionString, parsed, Policy, allowed, actor, token);

            // Approval preview sees no rows; in the rollback scenario a
            // concurrent committed insert forces statement two to fail
            // after statement one has executed inside a real transaction.
            if (!dropCommitAcknowledgement)
                await ExecAsync(independent,
                    "INSERT INTO " + "dbo." + table + "(id,note) VALUES(101,'racing-row')",
                    token);
            provider.Mode = dropCommitAcknowledgement
                ? LostAcknowledgementMode.Commit
                : LostAcknowledgementMode.Rollback;

            if (dropCommitAcknowledgement)
            {
                var unknown = await Assert.ThrowsAsync<DmlCommitOutcomeUnknownException>(
                    () => runtime.CommitTransactionAsync(
                        provider, connectionString, session, Policy, allowed, actor, token));
                Assert.IsType<IOException>(unknown.InnerException);
            }
            else
            {
                var unknown = await Assert.ThrowsAsync<DmlRollbackOutcomeUnknownException>(
                    () => runtime.CommitTransactionAsync(
                        provider, connectionString, session, Policy, allowed, actor, token));
                Assert.IsType<IOException>(unknown.RollbackFailure);
            }

            // Independent physical witness, not the wrapper's claimed status.
            await using (var witness = new SqlConnection(connectionString))
            {
                await witness.OpenAsync(token);
                Assert.Equal(dropCommitAcknowledgement ? 2L : 1L,
                    Convert.ToInt64(await ScalarAsync(witness,
                        "SELECT count(*) FROM " + "dbo." + table + "", token)));
                Assert.Equal(dropCommitAcknowledgement ? 1L : 0L,
                    Convert.ToInt64(await ScalarAsync(witness,
                        "SELECT count(*) FROM " + "dbo." + table + " WHERE id=100", token)));
                Assert.Equal(dropCommitAcknowledgement ? "second_value" : "racing-row",
                    (await ScalarAsync(witness,
                        "SELECT note FROM " + "dbo." + table + " WHERE id=101", token))?.ToString());
            }

            // One-time approval is consumed even when the application never
            // received confirmation for the real COMMIT/ROLLBACK.
            provider.Mode = LostAcknowledgementMode.None;
            var replay = await Assert.ThrowsAsync<InvalidOperationException>(
                () => runtime.CommitTransactionAsync(
                    provider, connectionString, session, Policy, allowed, actor, token));
            Assert.Contains("consumed", replay.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(dropCommitAcknowledgement ? 2L : 1L,
                Convert.ToInt64(await ScalarAsync(independent,
                    "SELECT count(*) FROM " + "dbo." + table + "", token)));
        }
        finally
        {
            await ExecAsync(independent, "DROP TABLE IF EXISTS dbo." + table,
                CancellationToken.None);
        }
    }

    private static async Task ExecAsync(SqlConnection db, string sql, CancellationToken token)
    {
        await using var cmd = new SqlCommand(sql, db);
        await cmd.ExecuteNonQueryAsync(token);
    }

    private static async Task<object?> ScalarAsync(
        SqlConnection db, string sql, CancellationToken token)
    {
        await using var cmd = new SqlCommand(sql, db);
        return await cmd.ExecuteScalarAsync(token);
    }

    private enum LostAcknowledgementMode { None, Commit, Rollback }

    // This is NOT an in-memory/mock transaction. Every command and COMMIT/
    // ROLLBACK is forwarded to a real file-backed Sql DB.
    // Only the acknowledgement after successful physical commit/rollback is
    // discarded to simulate a transport failure.
    private sealed class AcknowledgementDroppingMsSqlServerProvider : MsSqlServerProvider
    {
        public LostAcknowledgementMode Mode { get; set; }

        public override DbConnection CreateConnection(string? connectionString) =>
            Mode == LostAcknowledgementMode.None
                ? base.CreateConnection(connectionString)
                : new DroppedAckConnection(new SqlConnection(connectionString), Mode);
    }

    private sealed class DroppedAckConnection(
        SqlConnection inner, LostAcknowledgementMode mode) : DbConnection
    {
        [AllowNull]
        public override string ConnectionString
        {
            get => inner.ConnectionString;
            set => inner.ConnectionString = value ?? string.Empty;
        }
        internal SqlConnection Inner => inner;
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
            new DroppedAckTransaction(this, (SqlTransaction)inner.BeginTransaction(isolationLevel), mode);

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
        DroppedAckConnection owner, SqlTransaction inner, LostAcknowledgementMode mode)
        : DbTransaction
    {
        internal SqlTransaction Inner => inner;
        public override IsolationLevel IsolationLevel => inner.IsolationLevel;
        protected override DbConnection DbConnection => owner;
        public override void Commit()
        {
            inner.Commit();
            if (mode == LostAcknowledgementMode.Commit)
                throw new IOException("Simulated lost COMMIT acknowledgement after SQL Server committed.");
        }
        public override async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            await inner.CommitAsync(cancellationToken);
            if (mode == LostAcknowledgementMode.Commit)
                throw new IOException("Simulated lost COMMIT acknowledgement after SQL Server committed.");
        }
        public override void Rollback()
        {
            inner.Rollback();
            if (mode == LostAcknowledgementMode.Rollback)
                throw new IOException("Simulated lost ROLLBACK acknowledgement after SQL Server rolled back.");
        }
        public override async Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            await inner.RollbackAsync(cancellationToken);
            if (mode == LostAcknowledgementMode.Rollback)
                throw new IOException("Simulated lost ROLLBACK acknowledgement after SQL Server rolled back.");
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class DroppedAckCommand(DroppedAckConnection owner, SqlCommand inner) : DbCommand
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
                    inner.Connection = (SqlConnection?)value;
            }
        }
        protected override DbTransaction? DbTransaction
        {
            get => inner.Transaction;
            set => inner.Transaction = value switch
            {
                DroppedAckTransaction wrapped => wrapped.Inner,
                SqlTransaction sqlite => sqlite,
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
