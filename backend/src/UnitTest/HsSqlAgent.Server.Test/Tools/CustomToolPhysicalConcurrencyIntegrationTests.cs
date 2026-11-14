using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using HsSqlAgent.Provider.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

public sealed partial class CustomToolPhysicalAuditIntegrationTests
{
    // Identical published MCP load, approval, rejection, physical connection
    // and independent readback contract as the other five providers. SQLite
    // intentionally keeps its own native DbTransaction/DbCommand forwarding:
    // its BEGIN IMMEDIATE semantics must not be replaced by a generic wrapper.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    public Task RealSqliteUnderLoad_SingleSharedLeaseRejectsContendersWithoutOpeningTarget(
        int pauseOnPhysicalOpen) =>
        PhysicalLoadIntegrationScenario.UnderLoadAsync(
            async () => await Fixture.CreateAsync(),
            gate => new GatedSqliteProvider(gate),
            "audit-test-reviewer",
            pauseOnPhysicalOpen);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    public Task RealSqliteCancelledAtPhysicalOpen_ReleasesLeaseAndDoesNotMutateTarget(
        int pauseOnPhysicalOpen) =>
        PhysicalLoadIntegrationScenario.CancellationAsync(
            async () => await Fixture.CreateAsync(),
            gate => new GatedSqliteProvider(gate),
            "audit-test-reviewer",
            pauseOnPhysicalOpen);

    private sealed class GatedSqliteProvider(PhysicalTargetConnectionGate gate) : SqliteProvider
    {
        public override DbConnection CreateConnection(string? connectionString) =>
            new GatedTargetConnection(
                new SqliteConnection(connectionString),
                gate,
                gate.NextConnectionOrdinal());
    }


    /// <summary>Forwards real SQLite commands and transactions; instruments only open/close.</summary>
    private sealed class GatedTargetConnection(
        SqliteConnection inner, PhysicalTargetConnectionGate gate, int ordinal) : DbConnection
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
            gate.OnOpenedSync();
        }

        public override async Task OpenAsync(CancellationToken cancellationToken)
        {
            await inner.OpenAsync(cancellationToken);
            MarkOpen();
            gate.OnOpenedSync();
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
