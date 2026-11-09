using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Admin.Service.Interfaces;
using HsSqlAgent.Approvals;
using Admin.Service.Models;
using Admin.Service.Services;
using HsSqlAgent.Provider.MySql;
using HsSqlAgent.Server.Services;
using MySql.Data.MySqlClient;
using Moq;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

public sealed partial class MySqlPhysicalLoadIntegrationTests
{
    // The production admission gate budgets SQL *operations*, not a magic
    // provider-wide pool size. This test measures real target connections
    // rather than assuming a semaphore count equals physical DB connections.
    [Theory]
    [InlineData(1)] // Runtime-profile preflight
    [InlineData(2)] // First statement's live metadata/preview
    [InlineData(6)] // Approved atomic transaction's commit connection
    public async Task RealMySqlUnderLoad_SingleSharedLeaseRejectsContendersWithoutOpeningTarget(
        int pauseOnPhysicalOpen)
    {
        await using var fixture = await Fixture.CreateAsync();
        var gate = new TargetConnectionGate(pauseOnPhysicalOpen == 6 ? 0 : pauseOnPhysicalOpen);
        var provider = new GatedMySqlProvider(gate);
        var state = new SecurityPolicyRuntimeState();
        state.SetCurrent(new SecurityPolicyModel { MaxConcurrentSql = 1 });
        var limiter = new SqlExecutionConcurrencyLimiter(state);

        IDmlApprovalProvider approval = pauseOnPhysicalOpen == 6
            ? new CallbackApprovalProvider(() =>
            {
                gate.ArmForNextOpen();
                return Task.CompletedTask;
            })
            : new DecisionProvider(approve: true);
        var accepted = fixture.CreateProxy(
            approval, targetProvider: provider,
            concurrencyLimiter: limiter);
        var refused = fixture.CreateProxy(
            new DecisionProvider(approve: false),
            auditService: Mock.Of<IAuditService>(),
            targetProvider: provider,
            concurrencyLimiter: limiter);

        var request = JsonSerializer.SerializeToElement(new { });
        var inFlight = accepted.Execute(
            request, cancellationToken: TestContext.Current.CancellationToken);
        try
        {
            await gate.HeldOpen.Task.WaitAsync(
                TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Equal(1, limiter.ActiveCount);
            Assert.True(gate.ActivePhysicalTargetConnections > 0);
            var createdBeforeLoad = gate.FactoryCreates;

            // Compete with 24 independent MCP invocations while the first
            // request has physically opened the target and owns its one lease.
            var attempts = await Task.WhenAll(
                Enumerable.Range(0, 24).Select(_ => refused.Execute(
                    request, cancellationToken: TestContext.Current.CancellationToken)));

            Assert.All(attempts, item =>
                Assert.Contains("server.busy", item, StringComparison.Ordinal));
            Assert.Equal(createdBeforeLoad, gate.FactoryCreates);
            Assert.Equal(1, limiter.ActiveCount);
            Assert.True(gate.PeakPhysicalTargetConnections <= 1,
                $"A single leased operation opened {gate.PeakPhysicalTargetConnections} " +
                "target connections concurrently.");
        }
        finally
        {
            gate.Release();
        }

        var result = await inFlight;
        Assert.Contains("Success", result, StringComparison.Ordinal);
        Assert.Equal(2L, await fixture.CountRowsAsync());
        Assert.Equal(0, limiter.ActiveCount);
        Assert.Equal(0, gate.ActivePhysicalTargetConnections);
        Assert.True(gate.FactoryCreates >= (pauseOnPhysicalOpen == 6 ? 1 : pauseOnPhysicalOpen));
        if (pauseOnPhysicalOpen == 6)
            Assert.NotNull(Assert.IsType<CallbackApprovalProvider>(approval).LastRequest);

        // No one-time lease is stranded after returning the physical result.
        await using var next = await limiter.TryAcquireAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(next);
        Assert.Equal(1, limiter.ActiveCount);
    }

    [Theory]
    [InlineData(1)] // Cancel while real runtime-profile connection is opening
    [InlineData(2)] // Cancel while real preview connection is opening
    [InlineData(6)] // Cancel before the atomic commit transaction begins
    public async Task RealMySqlCancelledAtPhysicalOpen_ReleasesLeaseAndDoesNotMutateTarget(
        int pauseOnPhysicalOpen)
    {
        await using var fixture = await Fixture.CreateAsync();
        var gate = new TargetConnectionGate(pauseOnPhysicalOpen == 6 ? 0 : pauseOnPhysicalOpen);
        var provider = new GatedMySqlProvider(gate);
        var state = new SecurityPolicyRuntimeState();
        state.SetCurrent(new SecurityPolicyModel { MaxConcurrentSql = 1 });
        var limiter = new SqlExecutionConcurrencyLimiter(state);
        using var cancelled = new CancellationTokenSource();
        IDmlApprovalProvider approval = pauseOnPhysicalOpen == 6
            ? new CallbackApprovalProvider(() =>
            {
                gate.ArmForNextOpen();
                return Task.CompletedTask;
            })
            : new DecisionProvider(approve: true);

        var proxy = fixture.CreateProxy(
            approval,
            auditService: Mock.Of<IAuditService>(),
            targetProvider: provider,
            concurrencyLimiter: limiter);
        var inFlight = proxy.Execute(
            JsonSerializer.SerializeToElement(new { }),
            cancellationToken: cancelled.Token);

        try
        {
            await gate.HeldOpen.Task.WaitAsync(
                TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Equal(1, limiter.ActiveCount);
            Assert.True(gate.ActivePhysicalTargetConnections > 0);
            cancelled.Cancel();
        }
        finally
        {
            gate.Release();
        }

        var result = await inFlight;
        Assert.DoesNotContain("Success", result, StringComparison.Ordinal);
        Assert.Equal(0L, await fixture.CountRowsAsync());
        Assert.Equal(0, limiter.ActiveCount);
        Assert.Equal(0, gate.ActivePhysicalTargetConnections);

        await using var next = await limiter.TryAcquireAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(next);
    }

    private sealed class GatedMySqlProvider(TargetConnectionGate gate) : MySqlProvider
    {
        public override DbConnection CreateConnection(string? connectionString) =>
            new GatedTargetConnection(
                new MySqlConnection(connectionString),
                gate,
                gate.NextConnectionOrdinal());
    }

    private sealed class TargetConnectionGate(int pauseOnPhysicalOpen)
    {
        private readonly TaskCompletionSource<bool> _resume =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _created;
        private int _open;
        private int _peak;
        private int _pauseNextOpen;

        public TaskCompletionSource<bool> HeldOpen { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int FactoryCreates => Volatile.Read(ref _created);
        public int ActivePhysicalTargetConnections => Volatile.Read(ref _open);
        public int PeakPhysicalTargetConnections => Volatile.Read(ref _peak);

        public int NextConnectionOrdinal() => Interlocked.Increment(ref _created);
        public void ArmForNextOpen() => Interlocked.Exchange(ref _pauseNextOpen, 1);

        private void RecordPhysicalOpen()
        {
            var count = Interlocked.Increment(ref _open);
            var previousPeak = Volatile.Read(ref _peak);
            while (count > previousPeak)
            {
                var observed = Interlocked.CompareExchange(ref _peak, count, previousPeak);
                if (observed == previousPeak) break;
                previousPeak = observed;
            }

        }

        public void OnOpenedSync() => RecordPhysicalOpen();

        public async Task OnOpenedAsync(int ordinal, CancellationToken token)
        {
            if (ordinal == pauseOnPhysicalOpen || Interlocked.Exchange(ref _pauseNextOpen, 0) == 1)
            {
                HeldOpen.TrySetResult(true);
                await _resume.Task.WaitAsync(token);
            }
        }

        public void OnClosed() => Interlocked.Decrement(ref _open);
        public void Release() => _resume.TrySetResult(true);
    }


    private sealed class GatedTargetConnection(
        MySqlConnection inner, TargetConnectionGate gate, int ordinal) : DbConnection
    {
        private int _open;
        [AllowNull]
        public override string ConnectionString
        {
            get => inner.ConnectionString;
            set => inner.ConnectionString = value ?? string.Empty;
        }
        internal MySqlConnection Inner => inner;
        public override string Database => inner.Database;
        public override string DataSource => inner.DataSource;
        public override string ServerVersion => inner.ServerVersion;
        public override ConnectionState State => inner.State;
        public override int ConnectionTimeout => inner.ConnectionTimeout;
        public override void Open()
        {
            inner.Open();
            MarkOpen();
        }
        public override async Task OpenAsync(CancellationToken cancellationToken)
        {
            await inner.OpenAsync(cancellationToken);
            MarkOpen();
            await gate.OnOpenedAsync(ordinal, cancellationToken);
        }
        private void MarkOpen()
        {
            if (Interlocked.Exchange(ref _open, 1) != 0)
                throw new InvalidOperationException("A physical MySQL connection opened twice.");
            gate.OnOpenedSync();
        }
        private void MarkClosed()
        {
            if (Interlocked.Exchange(ref _open, 0) == 1)
                gate.OnClosed();
        }
        public override void Close()
        {
            inner.Close();
            MarkClosed();
        }
        public override void ChangeDatabase(string databaseName) => inner.ChangeDatabase(databaseName);

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            new GatedTargetTransaction(this, (MySqlTransaction)inner.BeginTransaction(isolationLevel));

        protected override DbCommand CreateDbCommand() =>
            new GatedTargetCommand(this, inner.CreateCommand());

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                MarkClosed();
            }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            MarkClosed();
        }
    }

    private sealed class GatedTargetTransaction(
        GatedTargetConnection owner, MySqlTransaction inner)
        : DbTransaction
    {
        internal MySqlTransaction Inner => inner;
        public override IsolationLevel IsolationLevel => inner.IsolationLevel;
        protected override DbConnection DbConnection => owner;
        public override void Commit() => inner.Commit();
        public override Task CommitAsync(CancellationToken cancellationToken = default) =>
            inner.CommitAsync(cancellationToken);
        public override void Rollback() => inner.Rollback();
        public override Task RollbackAsync(CancellationToken cancellationToken = default) =>
            inner.RollbackAsync(cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class GatedTargetCommand(GatedTargetConnection owner, MySqlCommand inner) : DbCommand
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
                if (value is GatedTargetConnection wrapped)
                    inner.Connection = wrapped.Inner;
                else
                    inner.Connection = (MySqlConnection?)value;
            }
        }
        protected override DbTransaction? DbTransaction
        {
            get => inner.Transaction;
            set => inner.Transaction = value switch
            {
                GatedTargetTransaction wrapped => wrapped.Inner,
                MySqlTransaction native => native,
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
