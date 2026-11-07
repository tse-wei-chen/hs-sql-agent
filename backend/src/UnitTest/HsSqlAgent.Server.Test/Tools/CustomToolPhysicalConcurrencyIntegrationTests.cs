using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Admin.Service.Services;
using HsSqlAgent.Provider.Sqlite;
using HsSqlAgent.Server.Services;
using Microsoft.Data.Sqlite;
using Moq;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

public sealed partial class CustomToolPhysicalAuditIntegrationTests
{
    // The production admission gate budgets SQL *operations*, not a magic
    // provider-wide pool size. This test measures real target connections
    // rather than assuming a semaphore count equals physical DB connections.
    [Theory]
    [InlineData(1)] // Runtime-profile preflight
    [InlineData(2)] // First statement's live metadata/preview
    [InlineData(6)] // Approved atomic transaction's commit connection
    public async Task RealSqliteUnderLoad_SingleSharedLeaseRejectsContendersWithoutOpeningTarget(
        int pauseOnPhysicalOpen)
    {
        await using var fixture = await Fixture.CreateAsync();
        var gate = new TargetConnectionGate(pauseOnPhysicalOpen);
        var provider = new GatedSqliteProvider(gate);
        var state = new SecurityPolicyRuntimeState();
        state.SetCurrent(new SecurityPolicyModel { MaxConcurrentSql = 1 });
        var limiter = new SqlExecutionConcurrencyLimiter(state);

        var accepted = fixture.CreateProxy(
            new DecisionProvider(approve: true), targetProvider: provider,
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
        Assert.True(gate.FactoryCreates >= pauseOnPhysicalOpen);

        // No one-time lease is stranded after returning the physical result.
        await using var next = await limiter.TryAcquireAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(next);
        Assert.Equal(1, limiter.ActiveCount);
    }

    [Theory]
    [InlineData(1)] // Cancel while real runtime-profile connection is opening
    [InlineData(2)] // Cancel while real preview connection is opening
    [InlineData(6)] // Cancel before the atomic commit transaction begins
    public async Task RealSqliteCancelledAtPhysicalOpen_ReleasesLeaseAndDoesNotMutateTarget(
        int pauseOnPhysicalOpen)
    {
        await using var fixture = await Fixture.CreateAsync();
        var gate = new TargetConnectionGate(pauseOnPhysicalOpen);
        var provider = new GatedSqliteProvider(gate);
        var state = new SecurityPolicyRuntimeState();
        state.SetCurrent(new SecurityPolicyModel { MaxConcurrentSql = 1 });
        var limiter = new SqlExecutionConcurrencyLimiter(state);
        using var cancelled = new CancellationTokenSource();

        var proxy = fixture.CreateProxy(
            new DecisionProvider(approve: true),
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

    private sealed class GatedSqliteProvider(TargetConnectionGate gate) : SqliteProvider
    {
        public override DbConnection CreateConnection(string? connectionString) =>
            new GatedTargetConnection(
                new SqliteConnection(connectionString),
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

        public TaskCompletionSource<bool> HeldOpen { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int FactoryCreates => Volatile.Read(ref _created);
        public int ActivePhysicalTargetConnections => Volatile.Read(ref _open);
        public int PeakPhysicalTargetConnections => Volatile.Read(ref _peak);

        public int NextConnectionOrdinal() => Interlocked.Increment(ref _created);

        public async Task OnOpenedAsync(int ordinal, CancellationToken token)
        {
            var count = Interlocked.Increment(ref _open);
            var previousPeak = Volatile.Read(ref _peak);
            while (count > previousPeak)
            {
                var observed = Interlocked.CompareExchange(ref _peak, count, previousPeak);
                if (observed == previousPeak) break;
                previousPeak = observed;
            }

            if (ordinal == pauseOnPhysicalOpen)
            {
                HeldOpen.TrySetResult(true);
                await _resume.Task.WaitAsync(token);
            }
        }

        public void OnClosed() => Interlocked.Decrement(ref _open);
        public void Release() => _resume.TrySetResult(true);
    }

    /// <summary>Forwards real SQLite commands and transactions; instruments only open/close.</summary>
    private sealed class GatedTargetConnection(
        SqliteConnection inner, TargetConnectionGate gate, int ordinal) : DbConnection
    {
        private int _physicalOpen;

        [AllowNull]
        public override string ConnectionString
        {
            get => inner.ConnectionString;
            set => inner.ConnectionString = value ?? string.Empty;
        }

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
            if (Interlocked.Exchange(ref _physicalOpen, 1) != 0)
                throw new InvalidOperationException("Physical connection opened twice.");
        }

        private void MarkClosed()
        {
            if (Interlocked.Exchange(ref _physicalOpen, 0) != 0)
                gate.OnClosed();
        }

        public override void Close()
        {
            MarkClosed();
            inner.Close();
        }

        public override void ChangeDatabase(string databaseName) =>
            inner.ChangeDatabase(databaseName);

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            inner.BeginTransaction(isolationLevel);

        protected override DbCommand CreateDbCommand() => inner.CreateCommand();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                MarkClosed();
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            MarkClosed();
            return inner.DisposeAsync();
        }
    }
}
