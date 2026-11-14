using System.Data.Common;
using HsSqlAgent.Provider.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

public sealed partial class SqlServerPhysicalLoadIntegrationTests
{
    // The published Custom MCP operation is admitted exactly once even while
    // 24 independent invocations contend for the same runtime lease.
    [Theory]
    [InlineData(1)] // Runtime-profile preflight
    [InlineData(2)] // Native DML preview metadata connection
    [InlineData(6)] // Atomic commit connection
    public Task RealSqlServerUnderLoad_SingleSharedLeaseRejectsContendersWithoutOpeningTarget(
        int pauseOnPhysicalOpen) =>
        PhysicalLoadIntegrationScenario.UnderLoadAsync(
            async () => await Fixture.CreateAsync(),
            gate => new GatedMsSqlServerProvider(gate),
            "sqlserver-load-prover",
            pauseOnPhysicalOpen);

    [Theory]
    [InlineData(1)] // Cancel while native preflight connection opens
    [InlineData(2)] // Cancel while native preview connection opens
    [InlineData(6)] // Cancel before atomic commit transaction
    public Task RealSqlServerCancelledAtPhysicalOpen_ReleasesLeaseAndDoesNotMutateTarget(
        int pauseOnPhysicalOpen) =>
        PhysicalLoadIntegrationScenario.CancellationAsync(
            async () => await Fixture.CreateAsync(),
            gate => new GatedMsSqlServerProvider(gate),
            "sqlserver-load-prover",
            pauseOnPhysicalOpen);

    private sealed class GatedMsSqlServerProvider(PhysicalTargetConnectionGate gate)
        : MsSqlServerProvider
    {
        public override DbConnection CreateConnection(string? connectionString) =>
            new PhysicalGatedTargetConnection(
                new SqlConnection(connectionString),
                gate,
                gate.NextConnectionOrdinal());
    }
}
