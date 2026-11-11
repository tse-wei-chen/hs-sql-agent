using System.Data.Common;
using HsSqlAgent.Provider.Oracle;
using Oracle.ManagedDataAccess.Client;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

public sealed partial class OraclePhysicalLoadIntegrationTests
{
    // The published Custom MCP operation is admitted exactly once even while
    // 24 independent invocations contend for the same runtime lease.
    [Theory]
    [InlineData(1)] // Runtime-profile preflight
    [InlineData(2)] // Native DML preview metadata connection
    [InlineData(6)] // Atomic commit connection
    public Task RealOracleUnderLoad_SingleSharedLeaseRejectsContendersWithoutOpeningTarget(
        int pauseOnPhysicalOpen) =>
        PhysicalLoadIntegrationScenario.UnderLoadAsync(
            async () => await Fixture.CreateAsync(),
            gate => new GatedOracleProvider(gate),
            "oracle-load-reviewer",
            pauseOnPhysicalOpen);

    [Theory]
    [InlineData(1)] // Cancel while native preflight connection opens
    [InlineData(2)] // Cancel while native preview connection opens
    [InlineData(6)] // Cancel before atomic commit transaction
    public Task RealOracleCancelledAtPhysicalOpen_ReleasesLeaseAndDoesNotMutateTarget(
        int pauseOnPhysicalOpen) =>
        PhysicalLoadIntegrationScenario.CancellationAsync(
            async () => await Fixture.CreateAsync(),
            gate => new GatedOracleProvider(gate),
            "oracle-load-reviewer",
            pauseOnPhysicalOpen);

    private sealed class GatedOracleProvider(PhysicalTargetConnectionGate gate)
        : OracleProvider
    {
        public override DbConnection CreateConnection(string? connectionString) =>
            new PhysicalGatedTargetConnection(
                new OracleConnection(connectionString),
                gate,
                gate.NextConnectionOrdinal());
    }
}
