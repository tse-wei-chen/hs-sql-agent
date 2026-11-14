using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class DmlTargetExecutionFenceRecoveryTests
{
    private static readonly DmlTargetCommitWitnessEvidence Evidence =
        new("fence-recovery-request", new string('B', 64));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revoke_DisposeFaultCannotChangeAcknowledgedOrUnknownCommit(bool lostCommitAcknowledgement)
    {
        await using var connection = new FaultInjectingFenceConnection(
            lostCommitAcknowledgement: lostCommitAcknowledgement);
        connection.Open();
        var fence = new RelationalDmlTargetExecutionFence();
        var ct = TestContext.Current.CancellationToken;

        if (lostCommitAcknowledgement)
        {
            var unknown = await Assert.ThrowsAsync<DmlCommitOutcomeUnknownException>(() =>
                fence.RevokeAsync(connection, SqlAgentToolType.Sqlite, Evidence, ct));
            Assert.IsType<IOException>(unknown.InnerException);
        }
        else
        {
            await fence.RevokeAsync(connection, SqlAgentToolType.Sqlite, Evidence, ct);
        }

        Assert.Equal(1, connection.InsertAttempts);
        Assert.Equal(1, connection.CommitAttempts);
    }

    [Fact]
    public async Task Revoke_FailedRollbackPlusDisposal_PreservesTypedUncertainty()
    {
        await using var connection = new FaultInjectingFenceConnection(
            mutationFailure: true, rollbackFailure: true);
        connection.Open();
        var error = await Assert.ThrowsAsync<DmlRollbackOutcomeUnknownException>(() =>
            new RelationalDmlTargetExecutionFence().RevokeAsync(
                connection, SqlAgentToolType.Sqlite, Evidence,
                TestContext.Current.CancellationToken));

        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.IsType<IOException>(error.RollbackFailure);
        Assert.Equal(1, connection.InsertAttempts);
        Assert.Equal(0, connection.CommitAttempts);
    }

    private sealed class FaultInjectingFenceConnection(
        bool lostCommitAcknowledgement = false,
        bool mutationFailure = false,
        bool rollbackFailure = false) : DbConnection
    {
        private ConnectionState _state = ConnectionState.Closed;
        public int InsertAttempts { get; set; }
        public int CommitAttempts { get; set; }
        public override string ConnectionString { get; set; } = "fake-fence";
        public override string Database => "fence-test";
        public override string DataSource => "fake";
        public override string ServerVersion => "1";
        public override ConnectionState State => _state;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() => _state = ConnectionState.Closed;
        public override void Open() => _state = ConnectionState.Open;

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            new FaultInjectingFenceTransaction(this, lostCommitAcknowledgement, rollbackFailure);

        protected override DbCommand CreateDbCommand() =>
            new FaultInjectingFenceCommand(this, mutationFailure);

        public override ValueTask DisposeAsync()
        {
            Close();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FaultInjectingFenceTransaction(
        FaultInjectingFenceConnection connection, bool lostCommitAcknowledgement,
        bool rollbackFailure) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.Serializable;
        protected override DbConnection DbConnection => connection;

        public override void Commit()
        {
            connection.CommitAttempts++;
            if (lostCommitAcknowledgement)
                throw new IOException("Simulated lost target COMMIT acknowledgement.");
        }
        public override Task CommitAsync(CancellationToken cancellationToken = default)
        {
            Commit();
            return Task.CompletedTask;
        }
        public override void Rollback()
        {
            if (rollbackFailure)
                throw new IOException("Simulated unconfirmed target rollback.");
        }
        public override Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            Rollback();
            return Task.CompletedTask;
        }
        public override ValueTask DisposeAsync() =>
            ValueTask.FromException(new IOException("Simulated transaction disposal failure."));
    }

    private sealed class FaultInjectingFenceCommand(
        FaultInjectingFenceConnection connection, bool mutationFailure) : DbCommand
    {
        private readonly SqliteCommand _parameters = new();
        protected override DbConnection DbConnection { get; set; } = connection;
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => _parameters.Parameters;
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; } = 30;
        public override CommandType CommandType { get; set; } = CommandType.Text;
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }

        public override void Prepare() { }
        public override void Cancel() { }
        protected override DbParameter CreateDbParameter() => new SqliteParameter();
        public override int ExecuteNonQuery()
        {
            connection.InsertAttempts++;
            if (mutationFailure)
                throw new InvalidOperationException("Simulated target fence insert failure.");
            return 1;
        }
        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ExecuteNonQuery());
        public override object? ExecuteScalar() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
            throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _parameters.Dispose();
            base.Dispose(disposing);
        }
    }
}
