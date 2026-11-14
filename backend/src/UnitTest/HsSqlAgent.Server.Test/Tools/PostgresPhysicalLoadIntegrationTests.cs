using System.Data.Common;
using HsSqlAgent.Provider.PostgreSql;
using Npgsql;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

public sealed partial class PostgresPhysicalLoadIntegrationTests
{
    // The published Custom MCP operation is admitted exactly once even while
    // 24 independent invocations contend for the same runtime lease.
    [Theory]
    [InlineData(1)] // Runtime-profile preflight
    [InlineData(2)] // Native DML preview metadata connection
    [InlineData(6)] // Atomic commit connection
    public Task RealPostgresUnderLoad_SingleSharedLeaseRejectsContendersWithoutOpeningTarget(
        int pauseOnPhysicalOpen) =>
        PhysicalLoadIntegrationScenario.UnderLoadAsync(
            async () => await Fixture.CreateAsync(),
            gate => new GatedPostgresProvider(gate),
            "postgres-load-prover",
            pauseOnPhysicalOpen);

    [Theory]
    [InlineData(1)] // Cancel while native preflight connection opens
    [InlineData(2)] // Cancel while native preview connection opens
    [InlineData(6)] // Cancel before atomic commit transaction
    public Task RealPostgresCancelledAtPhysicalOpen_ReleasesLeaseAndDoesNotMutateTarget(
        int pauseOnPhysicalOpen) =>
        PhysicalLoadIntegrationScenario.CancellationAsync(
            async () => await Fixture.CreateAsync(),
            gate => new GatedPostgresProvider(gate),
            "postgres-load-prover",
            pauseOnPhysicalOpen);

    private sealed class GatedPostgresProvider(PhysicalTargetConnectionGate gate)
        : PostgresProvider
    {
        public override DbConnection CreateConnection(string? connectionString) =>
            new PhysicalGatedTargetConnection(
                new NpgsqlConnection(connectionString),
                gate,
                gate.NextConnectionOrdinal());
    }
}
