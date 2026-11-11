using System.Data.Common;
using HsSqlAgent.Provider.Firebird;
using FirebirdSql.Data.FirebirdClient;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

public sealed partial class FirebirdPhysicalLoadIntegrationTests
{
    // The published Custom MCP operation is admitted exactly once even while
    // 24 independent invocations contend for the same runtime lease.
    [Theory]
    [InlineData(1)] // Runtime-profile preflight
    [InlineData(2)] // Native DML preview metadata connection
    [InlineData(6)] // Atomic commit connection
    public Task RealFirebirdUnderLoad_SingleSharedLeaseRejectsContendersWithoutOpeningTarget(
        int pauseOnPhysicalOpen) =>
        PhysicalLoadIntegrationScenario.UnderLoadAsync(
            async () => await Fixture.CreateAsync(),
            gate => new GatedFirebirdProvider(gate),
            "firebird-load-reviewer",
            pauseOnPhysicalOpen);

    [Theory]
    [InlineData(1)] // Cancel while native preflight connection opens
    [InlineData(2)] // Cancel while native preview connection opens
    [InlineData(6)] // Cancel before atomic commit transaction
    public Task RealFirebirdCancelledAtPhysicalOpen_ReleasesLeaseAndDoesNotMutateTarget(
        int pauseOnPhysicalOpen) =>
        PhysicalLoadIntegrationScenario.CancellationAsync(
            async () => await Fixture.CreateAsync(),
            gate => new GatedFirebirdProvider(gate),
            "firebird-load-reviewer",
            pauseOnPhysicalOpen);

    private sealed class GatedFirebirdProvider(PhysicalTargetConnectionGate gate)
        : FirebirdProvider
    {
        public override DbConnection CreateConnection(string? connectionString) =>
            new PhysicalGatedTargetConnection(
                new FbConnection(connectionString),
                gate,
                gate.NextConnectionOrdinal());
    }
}
