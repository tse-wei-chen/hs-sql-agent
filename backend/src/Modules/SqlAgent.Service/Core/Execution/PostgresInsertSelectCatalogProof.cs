using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HsSqlAgent.SqlCore.Core.Pipeline;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// Provider-native physical definition for M09 PostgreSQL INSERT SELECT.
/// No virtual catalog may mint permission. A regular permanent plain table
/// without rules, user triggers, inheritance, partitioning or RLS is required.
/// Column defaults, full index definitions and constraints participate in
/// the exact preview/commit digest; missing catalog visibility fails closed.
/// </summary>
internal static class PostgresInsertSelectCatalogProof
{
    internal static async Task<SqlSchemaSnapshot?> ExtendAsync(
        DbConnection connection, DbTransaction? transaction,
        SqlSchemaSnapshot basic, string schema, string table, CancellationToken token)
    {
        if (basic.Provider != SqlAgentToolType.Postgres || basic.Tables.Count != 1
            || !string.Equals(basic.Tables[0].Name, schema + "." + table, StringComparison.Ordinal))
            return null;

        const string baseIdentity = """
            SELECT c.oid::text, c.relkind::text, c.relpersistence::text,
                   c.relispartition, c.relhassubclass, c.relrowsecurity, c.relforcerowsecurity,
                   (SELECT count(*) FROM pg_catalog.pg_trigger t
                    WHERE t.tgrelid = c.oid AND NOT t.tgisinternal),
                   (SELECT count(*) FROM pg_catalog.pg_rewrite r
                    WHERE r.ev_class = c.oid)
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relname = @table
            """;

        await using var identityCommand = NewCommand(connection, transaction, baseIdentity, schema, table);
        await using var reader = await identityCommand.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)
            || reader.IsDBNull(0)
            || reader.GetString(1) != "r"
            || reader.GetString(2) != "p"
            || reader.GetBoolean(3) || reader.GetBoolean(4)
            || reader.GetBoolean(5) || reader.GetBoolean(6)
            || reader.GetInt64(7) != 0 || reader.GetInt64(8) != 0)
            return null;
        var oid = reader.GetString(0);
        if (await reader.ReadAsync(token)) return null;
        await reader.DisposeAsync();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "m09-native-postgres-plain-rel-v1");
        Append(hash, basic.Version);
        Append(hash, oid);

        string[] queries =
        [
            """
            SELECT a.attnum::text, a.attname, pg_catalog.format_type(a.atttypid, a.atttypmod),
                   a.attnotnull::text, a.attidentity::text, a.attgenerated::text,
                   a.attcollation::text, a.attisdropped::text,
                   pg_catalog.pg_get_expr(d.adbin, d.adrelid)
            FROM pg_catalog.pg_attribute a
            LEFT JOIN pg_catalog.pg_attrdef d
              ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            JOIN pg_catalog.pg_class c ON c.oid = a.attrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relname = @table AND a.attnum > 0
            ORDER BY a.attnum
            """,
            """
            SELECT i.indexrelid::text, pg_catalog.pg_get_indexdef(i.indexrelid),
                   i.indisvalid::text, i.indisready::text, i.indislive::text,
                   i.indisunique::text, i.indisprimary::text
            FROM pg_catalog.pg_index i
            JOIN pg_catalog.pg_class c ON c.oid = i.indrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relname = @table
            ORDER BY i.indexrelid
            """,
            """
            SELECT co.oid::text, co.conname, co.contype::text,
                   co.convalidated::text, pg_catalog.pg_get_constraintdef(co.oid, true)
            FROM pg_catalog.pg_constraint co
            JOIN pg_catalog.pg_class c ON c.oid = co.conrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relname = @table
            ORDER BY co.oid
            """
        ];
        for (var section = 0; section < queries.Length; section++)
        {
            await using var cmd = NewCommand(connection, transaction, queries[section], schema, table);
            await using var rows = await cmd.ExecuteReaderAsync(token);
            Append(hash, section.ToString(CultureInfo.InvariantCulture));
            var count = 0;
            while (await rows.ReadAsync(token))
            {
                Append(hash, "row");
                for (var col = 0; col < rows.FieldCount; col++)
                {
                    Append(hash, rows.IsDBNull(col)
                        ? "<NULL>"
                        : rows.GetValue(col) is string value ? value : "<UNPROVEN>");
                    if (!rows.IsDBNull(col) && rows.GetValue(col) is not string)
                        return null;
                }
                count++;
            }
            if (section == 0 && count == 0) return null;
            Append(hash, count.ToString(CultureInfo.InvariantCulture));
        }
        return new SqlSchemaSnapshot(SqlAgentToolType.Postgres,
            "native-postgres-insert-source-v1:" +
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            basic.Tables);
    }

    private static DbCommand NewCommand(DbConnection connection, DbTransaction? transaction,
        string sql, string schema, string table)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        var schemaParam = cmd.CreateParameter();
        schemaParam.ParameterName = "schema";
        schemaParam.Value = schema;
        cmd.Parameters.Add(schemaParam);
        var tableParam = cmd.CreateParameter();
        tableParam.ParameterName = "table";
        tableParam.Value = table;
        cmd.Parameters.Add(tableParam);
        return cmd;
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }
}
