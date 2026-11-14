using System.Data.Common;
using HsSqlAgent.Provider.MySql;
using MySql.Data.MySqlClient;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

public sealed partial class MySqlPhysicalLoadIntegrationTests
{
    // The published Custom MCP operation is admitted exactly once even while
    // 24 independent invocations contend for the same runtime lease.
    [Theory]
    [InlineData(1)] // Runtime-profile preflight
    [InlineData(2)] // Native DML preview metadata connection
    [InlineData(6)] // Atomic commit connection
    public Task RealMySqlUnderLoad_SingleSharedLeaseRejectsContendersWithoutOpeningTarget(
        int pauseOnPhysicalOpen) =>
        PhysicalLoadIntegrationScenario.UnderLoadAsync(
            async () => await Fixture.CreateAsync(),
            gate => new GatedMySqlProvider(gate),
            "mysql-load-prover",
            pauseOnPhysicalOpen);

    [Theory]
    [InlineData(1)] // Cancel while native preflight connection opens
    [InlineData(2)] // Cancel while native preview connection opens
    [InlineData(6)] // Cancel before atomic commit transaction
    public Task RealMySqlCancelledAtPhysicalOpen_ReleasesLeaseAndDoesNotMutateTarget(
        int pauseOnPhysicalOpen) =>
        PhysicalLoadIntegrationScenario.CancellationAsync(
            async () => await Fixture.CreateAsync(),
            gate => new GatedMySqlProvider(gate),
            "mysql-load-prover",
            pauseOnPhysicalOpen);

    private sealed class GatedMySqlProvider(PhysicalTargetConnectionGate gate)
        : MySqlProvider
    {
        public override DbConnection CreateConnection(string? connectionString) =>
            new PhysicalGatedTargetConnection(
                new MySqlConnection(connectionString),
                gate,
                gate.NextConnectionOrdinal());
    }
}
