using System.Data;
using System.Data.Common;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// Read-only PostgreSQL cutover check for a named retired worker role.
/// This is deliberately separate from the v3 worker's target-fence readiness:
/// successful admission by a new identity says nothing about old credentials.
///
/// The result is a point-in-time database-local table DML/session check, not
/// evidence about externally reachable writer nodes, SECURITY DEFINER
/// routines, other databases or revoked access after the snapshot.
/// </summary>
public sealed class PostgresLegacyDmlCutoverVerifier
{
    /// <summary>
    /// Reject unless the exact named legacy role has no effective
    /// INSERT/UPDATE/DELETE/TRUNCATE permissions (including column-level
    /// grants) on any non-system table/view/foreign
    /// table and no surviving PostgreSQL sessions in this database cluster.
    /// Requires a privileged monitoring connection, not the old worker login.
    /// This verifier does not revoke rights or terminate processes.
    /// </summary>
    public async Task<PostgresLegacyDmlCutoverEvidence> VerifyAsync(
        DbConnection openAdministrativeConnection,
        string legacyRole,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(openAdministrativeConnection);
        if (string.IsNullOrWhiteSpace(legacyRole) || legacyRole.Length > 63
            || legacyRole.IndexOf('\0') >= 0)
            throw new ArgumentException("A valid PostgreSQL legacy role name is required.", nameof(legacyRole));
        if (openAdministrativeConnection.State != ConnectionState.Open)
            throw new InvalidOperationException(
                "Legacy DML cutover verification requires an open PostgreSQL administrative connection.");

        // The monitoring role must be able to observe *other* sessions.
        // Without this, a session count is not a deployable quiescence proof.
        await using (var privileges = openAdministrativeConnection.CreateCommand())
        {
            privileges.CommandText = """
                SELECT COALESCE((
                    SELECT rolsuper FROM pg_catalog.pg_roles
                    WHERE rolname = CURRENT_USER
                ), FALSE)
                OR pg_catalog.pg_has_role(CURRENT_USER, 'pg_read_all_stats', 'MEMBER')
                """;
            if (await privileges.ExecuteScalarAsync(cancellationToken) is not true)
                throw new InvalidOperationException(
                    "Legacy DML cutover requires PostgreSQL superuser or pg_read_all_stats monitoring authority. " +
                    "A restricted session view must not produce a successful result.");
        }

        await using var command = openAdministrativeConnection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE legacy AS (
                SELECT oid, rolname FROM pg_catalog.pg_roles
                WHERE rolname = @legacy_role
            ), reachable_roles AS (
                SELECT oid FROM legacy
                UNION
                SELECT m.roleid FROM pg_catalog.pg_auth_members AS m
                JOIN reachable_roles AS r ON m.member = r.oid
            ), targets AS (
                SELECT c.oid
                FROM pg_catalog.pg_class AS c
                JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
                WHERE c.relkind IN ('r', 'p', 'f', 'v')
                  AND n.nspname <> 'information_schema'
                  AND n.nspname !~ '^pg_'
            )
            SELECT
                (SELECT COUNT(*) FROM legacy) AS role_count,
                COALESCE((SELECT BOOL_OR(r.rolsuper OR r.rolcreaterole
                    OR r.rolcreatedb OR r.rolreplication OR r.rolbypassrls)
                    FROM pg_catalog.pg_roles AS r
                    JOIN reachable_roles AS e ON e.oid = r.oid), FALSE) AS elevated,
                (SELECT COUNT(*) FROM pg_catalog.pg_stat_activity AS a
                    JOIN legacy AS l ON a.usesysid = l.oid) AS active_sessions,
                (SELECT COUNT(*) FROM targets) AS checked_relations,
                (SELECT COUNT(*) FROM targets AS t
                    WHERE EXISTS (
                        SELECT 1 FROM reachable_roles AS r
                        WHERE pg_catalog.has_table_privilege(r.oid, t.oid, 'INSERT')
                           OR pg_catalog.has_table_privilege(r.oid, t.oid, 'UPDATE')
                           OR pg_catalog.has_table_privilege(r.oid, t.oid, 'DELETE')
                           OR pg_catalog.has_table_privilege(r.oid, t.oid, 'TRUNCATE')
                           OR pg_catalog.has_any_column_privilege(r.oid, t.oid, 'INSERT')
                           OR pg_catalog.has_any_column_privilege(r.oid, t.oid, 'UPDATE')
                    )
                ) AS writable_relations
            """;
        var roleParameter = command.CreateParameter();
        roleParameter.ParameterName = "legacy_role";
        roleParameter.DbType = DbType.String;
        roleParameter.Value = legacyRole;
        command.Parameters.Add(roleParameter);
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Legacy DML cutover query returned no results.");

        var roleCount = reader.GetInt64(0);
        var elevated = reader.GetBoolean(1);
        var sessions = reader.GetInt64(2);
        var checkedRelations = reader.GetInt64(3);
        var writableRelations = reader.GetInt64(4);

        // An unknown old principal or empty catalog is an incomplete inventory,
        // not evidence that an old worker has lost its rights.
        if (roleCount != 1 || checkedRelations == 0)
            throw new InvalidOperationException(
                "Legacy DML cutover has an incomplete role or application-table inventory. " +
                "Verify the authoritative database and exact old worker identity.");
        if (elevated || sessions != 0 || writableRelations != 0)
            throw new InvalidOperationException(
                "Legacy DML cutover is blocked: elevatedRole=" + elevated +
                ", activeSessions=" + sessions +
                ", writableRelations=" + writableRelations +
                ". Revoke effective application-table privileges and terminate old sessions; " +
                "do not infer quiescence from the new worker's fence readiness.");

        return new PostgresLegacyDmlCutoverEvidence(
            legacyRole, checkedRelations, DateTimeOffset.UtcNow);
    }
}

/// <summary>
/// Point-in-time table-grant/session evidence for one PostgreSQL role.
/// Does not assert fleet-wide isolation or inspect SECURITY DEFINER routines.
/// </summary>
public sealed record PostgresLegacyDmlCutoverEvidence(
    string LegacyRole, long CheckedRelations, DateTimeOffset CheckedAtUtc);
