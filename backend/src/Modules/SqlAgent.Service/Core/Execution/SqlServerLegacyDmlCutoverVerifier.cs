using System.Data;
using System.Data.Common;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// Read-only SQL Server old-login table/column DML and active session check.
/// Deployment-time evidence on one authoritative database, NOT a fleet-wide
/// identity quarantine or assurance about stored procedure execution.
/// </summary>
public sealed class SqlServerLegacyDmlCutoverVerifier
{
    public async Task<SqlServerLegacyDmlCutoverEvidence> VerifyAsync(
        DbConnection openAdministrativeConnection,
        string legacyLogin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(openAdministrativeConnection);
        if (string.IsNullOrWhiteSpace(legacyLogin) || legacyLogin.Length > 128
            || legacyLogin.IndexOf('\0') >= 0)
            throw new ArgumentException("A valid SQL Server legacy login is required.", nameof(legacyLogin));
        if (openAdministrativeConnection.State != ConnectionState.Open)
            throw new InvalidOperationException(
                "SQL Server cutover requires an open connection to the authoritative target database.");

        // The verifier must see all sessions, metadata and role memberships
        // and be able to impersonate the old database principal. Elevated
        // security is an explicit deployment requirement, never a silent
        // fallback to a restricted catalog.
        await using (var authority = openAdministrativeConnection.CreateCommand())
        {
            authority.CommandText = "SELECT IS_SRVROLEMEMBER('sysadmin')";
            if (Convert.ToInt32(await authority.ExecuteScalarAsync(cancellationToken)) != 1)
                throw new InvalidOperationException(
                    "SQL Server cutover requires a sysadmin-owned, read-only verification session " +
                    "with complete login/session visibility and IMPERSONATE authority.");
        }

        // Verify a real mapped SQL login and refuse privileged principals
        // before impersonating. Null/unknown original login identity must
        // never be mistaken for evidence that an old worker cannot connect.
        await using var principal = openAdministrativeConnection.CreateCommand();
        principal.CommandText = """
            SELECT dp.name, sp.name
            FROM sys.server_principals AS sp
            JOIN sys.database_principals AS dp ON dp.sid = sp.sid
            WHERE sp.name = @login AND sp.type IN ('S', 'U')
              AND dp.type IN ('S', 'U')
            """;
        var loginParameter = principal.CreateParameter();
        loginParameter.ParameterName = "@login";
        loginParameter.DbType = DbType.String;
        loginParameter.Value = legacyLogin;
        principal.Parameters.Add(loginParameter);
        string mappedUser;
        await using (var reader = await principal.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException(
                    "SQL Server legacy login is missing or not mapped to a known target-database user.");
            mappedUser = reader.GetString(0);
            if (await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("SQL Server legacy login maps ambiguously to multiple users.");
        }

        await using var scope = openAdministrativeConnection.CreateCommand();
        scope.CommandText = """
            SELECT
              (SELECT COUNT_BIG(*) FROM sys.dm_exec_sessions
               WHERE original_login_name = @login) AS active_sessions,
              (SELECT COUNT_BIG(*) FROM sys.objects
               WHERE type IN ('U','V') AND is_ms_shipped = 0) AS object_count,
              (SELECT COUNT_BIG(*) FROM sys.synonyms) AS synonym_count,
              IS_SRVROLEMEMBER('sysadmin', @login) AS elevated_login
            """;
        var scopeParameter = scope.CreateParameter();
        scopeParameter.ParameterName = "@login";
        scopeParameter.DbType = DbType.String;
        scopeParameter.Value = legacyLogin;
        scope.Parameters.Add(scopeParameter);
        long activeSessions;
        long objectCount;
        long synonymCount;
        int elevated;
        await using (var reader = await scope.ExecuteReaderAsync(
                         CommandBehavior.SingleRow, cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("SQL Server catalog inspection returned no rows.");
            activeSessions = reader.GetInt64(0);
            objectCount = reader.GetInt64(1);
            synonymCount = reader.GetInt64(2);
            elevated = reader.IsDBNull(3) ? 1 : reader.GetInt32(3);
        }

        if (objectCount == 0 || objectCount > 10000 || synonymCount != 0)
            throw new InvalidOperationException(
                "SQL Server target inventory is empty, too large or contains synonyms which cannot be safely audited.");
        if (elevated != 0 || activeSessions != 0)
            throw new InvalidOperationException(
                $"SQL Server cutover blocked: privilegedLogin={elevated != 0}, activeSessions={activeSessions}. " +
                "Drain old sessions and remove privileged login authority before promotion.");

        var relations = new List<string>();
        await using (var inventory = openAdministrativeConnection.CreateCommand())
        {
            inventory.CommandText = """
                SELECT QUOTENAME(s.name) + '.' + QUOTENAME(o.name)
                FROM sys.objects AS o
                JOIN sys.schemas AS s ON o.schema_id = s.schema_id
                WHERE o.type IN ('U','V') AND o.is_ms_shipped = 0
                ORDER BY o.object_id
                """;
            await using var reader = await inventory.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                relations.Add(reader.GetString(0));
        }
        if (relations.Count != objectCount)
            throw new InvalidOperationException(
                "SQL Server target inventory changed during inspection. Repeat cutover verification.");

        // Read-only: impersonate the actual retired database user to ask the
        // SQL engine about *effective* rights including role inheritance,
        // column grants, DENY and fixed db roles. Object names are discovered
        // under admin authority, not from the restricted impersonated catalog.
        // REVERT is executed in T-SQL TRY/CATCH before any result is trusted.
        long writable = 0;
        foreach (var relation in relations)
        {
            await using var command = openAdministrativeConnection.CreateCommand();
            var escapedUser = mappedUser.Replace("'", "''", StringComparison.Ordinal);
            var escapedObject = relation.Replace("'", "''", StringComparison.Ordinal);
            command.CommandText = $"""
                BEGIN TRY
                    EXECUTE AS USER = N'{escapedUser}';
                    SELECT CASE
                        WHEN HAS_PERMS_BY_NAME(N'{escapedObject}', 'OBJECT', 'INSERT') = 1
                          OR HAS_PERMS_BY_NAME(N'{escapedObject}', 'OBJECT', 'UPDATE') = 1
                          OR HAS_PERMS_BY_NAME(N'{escapedObject}', 'OBJECT', 'DELETE') = 1
                          OR HAS_PERMS_BY_NAME(N'{escapedObject}', 'OBJECT', 'ALTER') = 1
                          OR HAS_PERMS_BY_NAME(N'{escapedObject}', 'OBJECT', 'CONTROL') = 1
                          OR EXISTS (
                              SELECT 1 FROM sys.columns AS c
                              WHERE c.object_id = OBJECT_ID(N'{escapedObject}')
                                AND (
                                     HAS_PERMS_BY_NAME(N'{escapedObject}.'
                                         + QUOTENAME(c.name), 'COLUMN', 'INSERT') = 1
                                  OR HAS_PERMS_BY_NAME(N'{escapedObject}.'
                                         + QUOTENAME(c.name), 'COLUMN', 'UPDATE') = 1
                                )
                          )
                        THEN 1 ELSE 0 END;
                    REVERT;
                END TRY
                BEGIN CATCH
                    REVERT;
                    THROW;
                END CATCH
                """;
            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (result is null or DBNull)
                throw new InvalidOperationException(
                    "SQL Server old-login impersonation returned no rights evaluation.");
            writable += Convert.ToInt32(result);
        }

        if (writable != 0)
            throw new InvalidOperationException(
                $"SQL Server legacy login still has effective DML or ALTER/CONTROL rights " +
                $"on {writable} target relations. Deny promotion and revoke effective write authority.");

        return new SqlServerLegacyDmlCutoverEvidence(
            legacyLogin, objectCount, DateTimeOffset.UtcNow);
    }
}

public sealed record SqlServerLegacyDmlCutoverEvidence(
    string LegacyLogin, long CheckedRelations, DateTimeOffset CheckedAtUtc);
