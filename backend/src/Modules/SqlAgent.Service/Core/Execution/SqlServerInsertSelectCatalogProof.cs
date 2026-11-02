using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HsSqlAgent.SqlCore.Core.Pipeline;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// Native SQL Server 2022 identity/definition attestation for one physical,
/// permanent ordinary table. This is a Host proof, never a virtual Catalog.
/// The same checks run at preview capture and beneath transaction-held X locks
/// at commit. Missing VIEW DEFINITION and unmodeled side effects deny approval.
/// </summary>
internal static class SqlServerInsertSelectCatalogProof
{
    internal static async Task<SqlSchemaSnapshot?> ExtendAsync(
        DbConnection connection, DbTransaction? transaction,
        SqlSchemaSnapshot basic, string schema, string table, CancellationToken token)
    {
        if (basic.Provider != SqlAgentToolType.MsSqlServer || basic.Tables.Count != 1
            || !string.Equals(schema, "dbo", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(basic.Tables[0].Name, schema + "." + table,
                StringComparison.OrdinalIgnoreCase))
            return null;

        // sys.tables excludes views/synonyms/external tables. Reject features
        // that could redirect writes or hide physical effects; catalog access
        // requires affirmative VIEW DEFINITION even for a metadata-visible name.
        const string identitySql = """
            SELECT t.object_id, CONVERT(int, SERVERPROPERTY('ProductMajorVersion')),
                   CONVERT(nvarchar(128), SERVERPROPERTY('ServerName')),
                   CONVERT(nvarchar(20), DB_ID()),
                   CONVERT(nvarchar(33), t.create_date, 126),
                   CONVERT(nvarchar(33), t.modify_date, 126),
                   HAS_PERMS_BY_NAME(QUOTENAME(@schema) + '.' + QUOTENAME(@table),
                                     'OBJECT', 'VIEW DEFINITION'),
                   (SELECT COUNT_BIG(*) FROM sys.triggers tr WHERE tr.parent_id = t.object_id),
                   (SELECT COUNT_BIG(*) FROM sys.foreign_keys fk
                    WHERE fk.parent_object_id = t.object_id OR fk.referenced_object_id = t.object_id),
                   (SELECT COUNT_BIG(*) FROM sys.check_constraints ck WHERE ck.parent_object_id = t.object_id),
                   (SELECT COUNT_BIG(*) FROM sys.default_constraints df WHERE df.parent_object_id = t.object_id),
                   (SELECT COUNT_BIG(*) FROM sys.security_predicates sp WHERE sp.target_object_id = t.object_id),
                   (SELECT COUNT_BIG(*) FROM sys.fulltext_indexes ft WHERE ft.object_id = t.object_id),
                   (SELECT COUNT_BIG(*) FROM sys.columns c
                    WHERE c.object_id = t.object_id AND
                      (c.is_computed = 1 OR c.is_hidden = 1 OR c.is_sparse = 1
                       OR c.is_column_set = 1 OR c.is_filestream = 1
                       OR c.generated_always_type <> 0 OR c.encryption_type IS NOT NULL
                       OR c.default_object_id <> 0 OR c.rule_object_id <> 0)),
                   (SELECT COUNT_BIG(*) FROM sys.indexes ix
                    WHERE ix.object_id = t.object_id
                      AND (ix.type NOT IN (1, 2) OR ix.is_disabled = 1
                           OR ix.is_hypothetical = 1 OR ix.has_filter = 1))
            FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = @schema AND t.name = @table
              AND t.is_ms_shipped = 0 AND t.is_memory_optimized = 0
              AND t.temporal_type = 0 AND t.ledger_type = 0
              AND t.is_filetable = 0 AND t.is_node = 0 AND t.is_edge = 0
              AND t.is_tracked_by_cdc = 0 AND t.is_replicated = 0
              AND t.is_remote_data_archive_enabled = 0
            """;
        int objectId;
        string[] physicalIdentity;
        await using (var cmd = NewCommand(connection, transaction, identitySql, schema, table))
        {
            await using var rows = await cmd.ExecuteReaderAsync(token);
            if (!await rows.ReadAsync(token) || rows.IsDBNull(0)
                || rows.IsDBNull(1) || rows.GetInt32(1) < 16
                || Enumerable.Range(2, 5).Any(rows.IsDBNull)
                || rows.GetInt32(6) != 1
                || Enumerable.Range(7, 8).Any(index => rows.IsDBNull(index)
                    || Convert.ToInt64(rows.GetValue(index), CultureInfo.InvariantCulture) != 0))
                return null;
            objectId = rows.GetInt32(0);
            physicalIdentity =
            [
                objectId.ToString(CultureInfo.InvariantCulture),
                rows.GetString(2),
                rows.GetString(3),
                rows.GetString(4),
                rows.GetString(5)
            ];
            if (await rows.ReadAsync(token)) return null;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "native-sqlserver2022-ordinary-table-v1");
        Append(hash, basic.Version);
        Append(hash, schema);
        Append(hash, table);
        foreach (var part in physicalIdentity) Append(hash, part);

        // Full ordered column/index/index-column/key definitions. SQL Server
        // will hold Sch-S while reading and commit holds table X locks first.
        string[] sections =
        [
            """
            SELECT CONVERT(nvarchar(12),c.column_id), c.name,
                   CONVERT(nvarchar(12),c.system_type_id),
                   CONVERT(nvarchar(12),c.user_type_id), ty.name,
                   CONVERT(nvarchar(12),c.max_length),
                   CONVERT(nvarchar(12),c.precision),
                   CONVERT(nvarchar(12),c.scale), c.collation_name,
                   CONVERT(nvarchar(2),c.is_nullable), CONVERT(nvarchar(2),c.is_identity),
                   CONVERT(nvarchar(2),c.is_rowguidcol)
            FROM sys.columns c
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE c.object_id = @oid ORDER BY c.column_id
            """,
            """
            SELECT CONVERT(nvarchar(12),i.index_id), i.name, i.type_desc,
                   CONVERT(nvarchar(2),i.is_unique),
                   CONVERT(nvarchar(2),i.is_primary_key),
                   CONVERT(nvarchar(2),i.is_unique_constraint),
                   CONVERT(nvarchar(2),i.ignore_dup_key),
                   CONVERT(nvarchar(2),i.allow_row_locks),
                   CONVERT(nvarchar(2),i.allow_page_locks)
            FROM sys.indexes i WHERE i.object_id = @oid ORDER BY i.index_id
            """,
            """
            SELECT CONVERT(nvarchar(12),ic.index_id),
                   CONVERT(nvarchar(12),ic.index_column_id),
                   CONVERT(nvarchar(12),ic.column_id),
                   CONVERT(nvarchar(12),ic.key_ordinal),
                   CONVERT(nvarchar(2),ic.is_descending_key),
                   CONVERT(nvarchar(2),ic.is_included_column),
                   CONVERT(nvarchar(12),ic.partition_ordinal)
            FROM sys.index_columns ic WHERE ic.object_id = @oid
            ORDER BY ic.index_id,ic.index_column_id
            """,
            """
            SELECT CONVERT(nvarchar(12),kc.object_id), kc.name, kc.type,
                   CONVERT(nvarchar(12),kc.unique_index_id)
            FROM sys.key_constraints kc WHERE kc.parent_object_id = @oid
            ORDER BY kc.object_id
            """
        ];
        for (var section = 0; section < sections.Length; section++)
        {
            await using var cmd = NewCommand(connection, transaction, sections[section], schema, table, objectId);
            await using var rows = await cmd.ExecuteReaderAsync(token);
            Append(hash, section.ToString(CultureInfo.InvariantCulture));
            var count = 0;
            while (await rows.ReadAsync(token))
            {
                Append(hash, "row");
                for (var field = 0; field < rows.FieldCount; field++)
                {
                    if (rows.IsDBNull(field)) Append(hash, "NULL");
                    else
                    {
                        if (rows.GetValue(field) is not string value) return null;
                        Append(hash, "value");
                        Append(hash, value);
                    }
                }
                count++;
            }
            if (section == 0 && count == 0) return null;
            Append(hash, count.ToString(CultureInfo.InvariantCulture));
        }

        return new SqlSchemaSnapshot(SqlAgentToolType.MsSqlServer,
            "native-sqlserver2022-insert-source-v1:" +
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            basic.Tables);
    }

    private static DbCommand NewCommand(DbConnection connection, DbTransaction? transaction,
        string sql, string schema, string table, int? objectId = null)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandTimeout = 15;
        cmd.CommandText = sql;
        static void Add(DbCommand cmd, string key, object value)
        {
            var parameter = cmd.CreateParameter();
            parameter.ParameterName = key;
            parameter.Value = value;
            cmd.Parameters.Add(parameter);
        }
        Add(cmd, "@schema", schema);
        Add(cmd, "@table", table);
        if (objectId.HasValue) Add(cmd, "@oid", objectId.Value);
        return cmd;
    }

    private static void Append(IncrementalHash hash, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }
}
