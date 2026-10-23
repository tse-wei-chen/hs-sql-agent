using Npgsql;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Mandatory in CI: reads real PostgreSQL catalogs and session state,
/// including SET ROLE / NOINHERIT privilege paths. Never performs business
/// DML for the permission checks; all setup uses isolated test objects.
/// </summary>
public sealed class PostgresLegacyDmlCutoverVerifierIntegrationTests
{
    [Fact]
    public async Task RetiredRole_GrantsAndLiveConnectionsBlockUntilBothAreGone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var target = await Target.CreateAsync(ct);
        await using var admin = await target.OpenAdminAsync(ct);
        var verifier = new PostgresLegacyDmlCutoverVerifier();

        await ExecuteAsync(admin,
            $"GRANT USAGE ON SCHEMA {target.SchemaName} TO {target.LegacyRole}; " +
            $"GRANT INSERT, UPDATE, DELETE ON {target.SchemaName}.application_data TO {target.LegacyRole}", ct);

        var unsafeGrant = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            verifier.VerifyAsync(admin, target.LegacyRole, ct));
        Assert.Contains("writableRelations=", unsafeGrant.Message);

        await using (var oldSession = new NpgsqlConnection(target.LegacyConnectionString))
        {
            await oldSession.OpenAsync(ct);
            await ExecuteAsync(admin,
                $"REVOKE INSERT, UPDATE, DELETE ON {target.SchemaName}.application_data " +
                $"FROM {target.LegacyRole}", ct);

            var unsafeSession = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                verifier.VerifyAsync(admin, target.LegacyRole, ct));
            Assert.Contains("activeSessions=", unsafeSession.Message);
        }

        // Column-specific UPDATE grants must also block the cutover.
        await ExecuteAsync(admin,
            $"GRANT UPDATE (id) ON {target.SchemaName}.application_data TO {target.LegacyRole}", ct);
        var columnGrant = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            verifier.VerifyAsync(admin, target.LegacyRole, ct));
        Assert.Contains("writableRelations=", columnGrant.Message);
        await ExecuteAsync(admin,
            $"REVOKE UPDATE (id) ON {target.SchemaName}.application_data FROM {target.LegacyRole}", ct);

        // PostgreSQL TRUNCATE is destructive, even if a DML compiler would
        // not accept that statement shape.
        await ExecuteAsync(admin,
            $"GRANT TRUNCATE ON {target.SchemaName}.application_data TO {target.LegacyRole}", ct);
        var truncateGrant = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            verifier.VerifyAsync(admin, target.LegacyRole, ct));
        Assert.Contains("writableRelations=", truncateGrant.Message);
        await ExecuteAsync(admin,
            $"REVOKE TRUNCATE ON {target.SchemaName}.application_data FROM {target.LegacyRole}", ct);

        var liveCredential = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            verifier.VerifyAsync(admin, target.LegacyRole, ct));
        Assert.Contains("loginEnabled=True", liveCredential.Message);
        await ExecuteAsync(admin, $"ALTER ROLE {target.LegacyRole} NOLOGIN", ct);
        var cleared = await verifier.VerifyAsync(admin, target.LegacyRole, ct);
        Assert.Equal(target.LegacyRole, cleared.LegacyRole);
        Assert.True(cleared.CheckedRelations >= 1);
        Assert.True(cleared.CheckedAtUtc <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task NoInheritRole_StillRejectedForPrivilegesAvailableViaSetRole()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var target = await Target.CreateAsync(ct);
        await using var admin = await target.OpenAdminAsync(ct);
        var verifier = new PostgresLegacyDmlCutoverVerifier();

        await ExecuteAsync(admin,
            $"CREATE ROLE {target.ParentRole} NOLOGIN; " +
            $"GRANT USAGE ON SCHEMA {target.SchemaName} TO {target.ParentRole}; " +
            $"GRANT UPDATE ON {target.SchemaName}.application_data TO {target.ParentRole}; " +
            $"GRANT {target.ParentRole} TO {target.LegacyRole}", ct);
        target.ParentCreated = true;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            verifier.VerifyAsync(admin, target.LegacyRole, ct));
        Assert.Contains("writableRelations=", failure.Message);

        await ExecuteAsync(admin,
            $"REVOKE UPDATE ON {target.SchemaName}.application_data FROM {target.ParentRole}; " +
            $"ALTER ROLE {target.LegacyRole} NOLOGIN", ct);
        var cleared = await verifier.VerifyAsync(admin, target.LegacyRole, ct);
        Assert.True(cleared.CheckedRelations >= 1);
    }

    [Fact]
    public async Task CallableSecurityDefinerRoutine_BlocksCutoverUntilRevoked()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var target = await Target.CreateAsync(ct);
        await using var admin = await target.OpenAdminAsync(ct);
        var verifier = new PostgresLegacyDmlCutoverVerifier();

        // PostgreSQL functions grant PUBLIC EXECUTE by default. A routine
        // executed with the owner's elevated rights is an independent path.
        await ExecuteAsync(admin,
            $"CREATE FUNCTION {target.SchemaName}.legacy_routine() RETURNS integer " +
            "LANGUAGE sql SECURITY DEFINER AS 'SELECT 1'; " +
            $"ALTER ROLE {target.LegacyRole} NOLOGIN", ct);
        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            verifier.VerifyAsync(admin, target.LegacyRole, ct));
        Assert.Contains("callableDefinerRoutines=", blocked.Message);
        Assert.DoesNotContain("callableDefinerRoutines=0", blocked.Message);

        await ExecuteAsync(admin,
            $"REVOKE EXECUTE ON FUNCTION {target.SchemaName}.legacy_routine() FROM PUBLIC", ct);
        var cleared = await verifier.VerifyAsync(admin, target.LegacyRole, ct);
        Assert.Equal(target.LegacyRole, cleared.LegacyRole);
    }

    [Fact]
    public async Task UnrecognizedOrPrivilegedOldPrincipal_NeverPasses()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var target = await Target.CreateAsync(ct);
        await using var admin = await target.OpenAdminAsync(ct);
        var verifier = new PostgresLegacyDmlCutoverVerifier();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            verifier.VerifyAsync(admin, "missing_hsa_legacy_role", ct));

        await using var username = admin.CreateCommand();
        username.CommandText = "SELECT CURRENT_USER";
        var rootRole = (string)(await username.ExecuteScalarAsync(ct))!;
        var rootError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            verifier.VerifyAsync(admin, rootRole, ct));
        Assert.Contains("elevatedRole=True", rootError.Message);

        await using var unprivileged = new NpgsqlConnection(target.LegacyConnectionString);
        await unprivileged.OpenAsync(ct);
        var monitoringError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            verifier.VerifyAsync(unprivileged, target.LegacyRole, ct));
        Assert.Contains("monitoring authority", monitoringError.Message);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private sealed class Target(string rootConnectionString, string name)
        : IAsyncDisposable
    {
        public string SchemaName { get; } = "hsa_cutover_" + name;
        public string LegacyRole { get; } = "hsa_legacy_" + name;
        public string ParentRole { get; } = "hsa_parent_" + name;
        public bool ParentCreated { get; set; }

        public string LegacyConnectionString => new NpgsqlConnectionStringBuilder(rootConnectionString)
        {
            Username = LegacyRole,
            Password = "LegacyTestOnly-123!",
            Pooling = false
        }.ConnectionString;

        public static async Task<Target> CreateAsync(CancellationToken ct)
        {
            var cs = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
            if (string.IsNullOrWhiteSpace(cs))
            {
                if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                    StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "TEST_POSTGRES_CONNECTION must be configured for legacy cutover verification in CI.");
                Assert.Skip("Real PostgreSQL legacy cutover test requires TEST_POSTGRES_CONNECTION");
            }

            var target = new Target(cs, Guid.NewGuid().ToString("N")[..14]);
            await using var admin = await target.OpenAdminAsync(ct);
            await ExecuteAsync(admin, $"CREATE SCHEMA {target.SchemaName}; " +
                $"CREATE TABLE {target.SchemaName}.application_data (id integer PRIMARY KEY); " +
                $"CREATE ROLE {target.LegacyRole} LOGIN NOINHERIT PASSWORD 'LegacyTestOnly-123!'", ct);
            return target;
        }

        public async Task<NpgsqlConnection> OpenAdminAsync(CancellationToken ct)
        {
            var c = new NpgsqlConnection(rootConnectionString);
            await c.OpenAsync(ct);
            return c;
        }

        public async ValueTask DisposeAsync()
        {
            await using var admin = await OpenAdminAsync(CancellationToken.None);
            await ExecuteAsync(admin, $"DROP SCHEMA IF EXISTS {SchemaName} CASCADE", CancellationToken.None);
            await ExecuteAsync(admin, $"DROP ROLE IF EXISTS {LegacyRole}", CancellationToken.None);
            if (ParentCreated)
                await ExecuteAsync(admin, $"DROP ROLE IF EXISTS {ParentRole}", CancellationToken.None);
        }
    }
}
