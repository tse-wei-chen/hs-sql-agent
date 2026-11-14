using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace HsSqlAgent.Server.Services;

/// <summary>
/// Bounded, live SQL Server effective-permission check for a Query without
/// a Table Whitelist. This is NOT a SQL Server read-only transaction or a
/// proof against CLR/external effects, cross-database module escalation or
/// permissions being changed after this check. It avoids silently accepting
/// an all-powerful SQL Server login as a Query-only database principal.
/// </summary>
internal static class SqlServerQueryPrincipalGuard
{
    // SQL comes exclusively from this fixed literal. Only server-discovered,
    // QUOTENAME-escaped object identifiers are evaluated by SQL Server. No
    // user SQL, identity or database name enters this attestation statement.
    //
    // VIEW DEFINITION is mandatory: sys.objects is otherwise filtered by
    // metadata visibility and a partial catalog cannot prove a negative.
    //
    // fn_my_permissions reports effective inherited DATABASE/SERVER grants
    // (including fixed role implications). HAS_PERMS_BY_NAME checks each
    // visible object and column for independent object-scoped grants.
    // Refuse user-defined CLR assemblies and synonyms: their external,
    // dynamic and cross-database semantics are not proven by this scan.
    private const string EffectivePrivilegeProbe = """
        SELECT CASE WHEN
            DB_NAME() IS NULL OR USER_NAME() IS NULL
            OR ISNULL(IS_SRVROLEMEMBER('sysadmin'), 1) <> 0
            OR ISNULL(IS_SRVROLEMEMBER('securityadmin'), 1) <> 0
            OR ISNULL(IS_SRVROLEMEMBER('serveradmin'), 1) <> 0
            OR ISNULL(IS_SRVROLEMEMBER('setupadmin'), 1) <> 0
            OR ISNULL(IS_SRVROLEMEMBER('dbcreator'), 1) <> 0
            OR ISNULL(IS_SRVROLEMEMBER('bulkadmin'), 1) <> 0
            OR ISNULL(IS_MEMBER('db_owner'), 1) <> 0
            OR ISNULL(IS_MEMBER('db_datawriter'), 1) <> 0
            OR ISNULL(IS_MEMBER('db_ddladmin'), 1) <> 0
            OR ISNULL(IS_MEMBER('db_securityadmin'), 1) <> 0
            OR ISNULL(IS_MEMBER('db_accessadmin'), 1) <> 0
            OR ISNULL(IS_MEMBER('db_backupoperator'), 1) <> 0
            OR ISNULL(
                HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION'), 0) <> 1
            OR EXISTS (
                SELECT 1 FROM sys.fn_my_permissions(NULL, 'DATABASE')
                WHERE permission_name NOT IN (
                    N'CONNECT', N'SELECT', N'VIEW DEFINITION',
                    N'VIEW SECURITY DEFINITION', N'VIEW PERFORMANCE DEFINITION',
                    N'VIEW DATABASE STATE', N'SHOWPLAN',
                    N'VIEW ANY COLUMN MASTER KEY DEFINITION',
                    N'VIEW ANY COLUMN ENCRYPTION KEY DEFINITION'
                )
            )
            OR EXISTS (
                SELECT 1 FROM sys.fn_my_permissions(NULL, 'SERVER')
                WHERE permission_name NOT IN (
                    N'CONNECT SQL', N'VIEW ANY DATABASE',
                    N'VIEW ANY COLUMN MASTER KEY DEFINITION',
                    N'VIEW ANY COLUMN ENCRYPTION KEY DEFINITION'
                )
            )
            OR EXISTS (
                SELECT 1
                FROM sys.schemas AS s
                WHERE s.name NOT IN (N'sys', N'INFORMATION_SCHEMA')
                  AND (
                      HAS_PERMS_BY_NAME(QUOTENAME(s.name), 'SCHEMA', 'ALTER') = 1
                   OR HAS_PERMS_BY_NAME(QUOTENAME(s.name), 'SCHEMA', 'CONTROL') = 1
                   OR HAS_PERMS_BY_NAME(QUOTENAME(s.name), 'SCHEMA', 'TAKE OWNERSHIP') = 1
                   OR HAS_PERMS_BY_NAME(QUOTENAME(s.name), 'SCHEMA', 'EXECUTE') = 1
                  )
            )
            OR EXISTS (
                SELECT 1 FROM sys.objects AS o
                INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
                WHERE o.is_ms_shipped = 0
                  AND (
                      HAS_PERMS_BY_NAME(
                          QUOTENAME(s.name) + N'.' + QUOTENAME(o.name),
                          'OBJECT', 'INSERT') = 1
                   OR HAS_PERMS_BY_NAME(
                          QUOTENAME(s.name) + N'.' + QUOTENAME(o.name),
                          'OBJECT', 'UPDATE') = 1
                   OR HAS_PERMS_BY_NAME(
                          QUOTENAME(s.name) + N'.' + QUOTENAME(o.name),
                          'OBJECT', 'DELETE') = 1
                   OR HAS_PERMS_BY_NAME(
                          QUOTENAME(s.name) + N'.' + QUOTENAME(o.name),
                          'OBJECT', 'ALTER') = 1
                   OR HAS_PERMS_BY_NAME(
                          QUOTENAME(s.name) + N'.' + QUOTENAME(o.name),
                          'OBJECT', 'CONTROL') = 1
                   OR HAS_PERMS_BY_NAME(
                          QUOTENAME(s.name) + N'.' + QUOTENAME(o.name),
                          'OBJECT', 'TAKE OWNERSHIP') = 1
                   OR (o.type IN (
                          'P', 'PC', 'FN', 'FS', 'IF', 'TF', 'FT', 'AF', 'X')
                       AND HAS_PERMS_BY_NAME(
                          QUOTENAME(s.name) + N'.' + QUOTENAME(o.name),
                          'OBJECT', 'EXECUTE') = 1)
                  )
            )
            OR EXISTS (
                SELECT 1 FROM sys.columns AS c
                INNER JOIN sys.objects AS o ON o.object_id = c.object_id
                INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
                WHERE o.is_ms_shipped = 0 AND o.type IN ('U', 'V')
                  AND (
                      HAS_PERMS_BY_NAME(
                          QUOTENAME(s.name) + N'.' + QUOTENAME(o.name),
                          'OBJECT', 'UPDATE', c.name, 'COLUMN') = 1
                   OR HAS_PERMS_BY_NAME(
                          QUOTENAME(s.name) + N'.' + QUOTENAME(o.name),
                          'OBJECT', 'INSERT', c.name, 'COLUMN') = 1
                  )
            )
            OR EXISTS (SELECT 1 FROM sys.synonyms WHERE is_ms_shipped = 0)
            OR EXISTS (SELECT 1 FROM sys.assemblies WHERE is_user_defined = 1)
            THEN CAST(1 AS int)
            ELSE CAST(0 AS int)
        END
        """;

    internal static async Task VerifyAsync(
        DbConnection connection,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection is not SqlConnection || connection.State != ConnectionState.Open)
            throw new UnauthorizedAccessException(
                "SQL Server unbounded Query requires an open native SQL Server connection for database permission verification.");

        await using var probe = connection.CreateCommand();
        probe.CommandText = EffectivePrivilegeProbe;
        probe.CommandTimeout = timeoutSeconds > 0 ? timeoutSeconds : 30;

        // NULL, errors, insufficient metadata permissions and unknown proof
        // results cannot grant Query execution. The caller must not retry the
        // compiled SELECT through the generic, unchecked Query executor.
        var proof = await probe.ExecuteScalarAsync(cancellationToken);
        if (proof is not int verdict || verdict != 0)
            throw new UnauthorizedAccessException(
                "SQL Server Query without a Table Whitelist requires a dedicated database reader "
                + "with database-wide VIEW DEFINITION, no server/database elevation, "
                + "and no effective object/column DML or routine EXECUTE grants. "
                + "Use a separate limited SQL Server login instead of privileged DML credentials.");
    }
}
