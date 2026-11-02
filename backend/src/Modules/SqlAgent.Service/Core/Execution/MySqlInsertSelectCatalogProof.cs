using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using HsSqlAgent.SqlCore.Core.Pipeline;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// Bounded native MySQL 8.4 InnoDB INSERT SELECT proof.
/// This is NOT a virtual-catalog assertion. The actual permanent table,
/// engine, DDL, indexes, constraints and trigger visibility must be verified.
/// Capture outside the approval transaction is never a commit authority.
/// </summary>
internal static class MySqlInsertSelectCatalogProof
{
    internal static async Task<SqlSchemaSnapshot?> ExtendAsync(
        DbConnection connection, DbTransaction? transaction,
        SqlSchemaSnapshot basic, string schema, string table, CancellationToken token)
    {
        if (basic.Provider != SqlAgentToolType.MySQL || basic.Tables.Count != 1
            || !string.Equals(basic.Tables[0].Name, schema + "." + table, StringComparison.Ordinal)
            || !Version.TryParse(connection.ServerVersion.Split('-', 2)[0], out var version)
            || version < new Version(8, 4))
            return null;

        await using (var info = Command(connection, transaction, """
            SELECT TABLE_NAME, TABLE_TYPE, ENGINE
            FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table
            """, schema, table))
        await using (var rows = await info.ExecuteReaderAsync(token))
        {
            if (!await rows.ReadAsync(token)
                || !string.Equals(rows.GetString(0), table, StringComparison.Ordinal)
                || !string.Equals(rows.GetString(1), "BASE TABLE", StringComparison.Ordinal)
                || !string.Equals(rows.GetString(2), "InnoDB", StringComparison.OrdinalIgnoreCase)
                || await rows.ReadAsync(token))
                return null;
        }

        // MySQL trigger catalog entries may be hidden from a caller without
        // TRIGGER permission. Only a user granted global TRIGGER visibility can
        // assert absence; least-privilege users remain safely unsupported.
        await using (var grants = Command(connection, transaction, """
            SELECT COUNT(*)
            FROM information_schema.USER_PRIVILEGES
            WHERE PRIVILEGE_TYPE = 'TRIGGER'
              AND GRANTEE = CONCAT(
                  QUOTE(SUBSTRING_INDEX(CURRENT_USER(), '@', 1)), '@',
                  QUOTE(SUBSTRING_INDEX(CURRENT_USER(), '@', -1)))
            """, schema, table))
        {
            if (Convert.ToInt64(await grants.ExecuteScalarAsync(token)) <= 0)
                return null;
        }

        await using (var excluded = Command(connection, transaction, """
            SELECT
                (SELECT COUNT(*) FROM information_schema.TRIGGERS
                 WHERE TRIGGER_SCHEMA = @schema AND EVENT_OBJECT_TABLE = @table)
              + (SELECT COUNT(*) FROM information_schema.KEY_COLUMN_USAGE
                 WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table
                   AND REFERENCED_TABLE_NAME IS NOT NULL)
              + (SELECT COUNT(*) FROM information_schema.KEY_COLUMN_USAGE
                 WHERE REFERENCED_TABLE_SCHEMA = @schema AND REFERENCED_TABLE_NAME = @table)
            """, schema, table))
        {
            if (Convert.ToInt64(await excluded.ExecuteScalarAsync(token)) != 0)
                return null;
        }

        // MySQL SHOW CREATE TABLE captures the complete defining table SQL:
        // columns/defaults, indexes, checks, collation and constraints.
        // AUTO_INCREMENT may change for unrelated inserts, in which case the
        // later commit conservatively rejects rather than ignoring drift.
        static string Quote(string value) => "`" + value.Replace("`", "``", StringComparison.Ordinal) + "`";
        await using var ddl = connection.CreateCommand();
        ddl.Transaction = transaction;
        ddl.CommandText = $"SHOW CREATE TABLE {Quote(schema)}.{Quote(table)}";
        await using var reader = await ddl.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)
            || !string.Equals(reader.GetString(0), table, StringComparison.Ordinal)
            || reader.IsDBNull(1))
            return null;
        var definition = reader.GetString(1);
        if (string.IsNullOrWhiteSpace(definition) || await reader.ReadAsync(token))
            return null;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "m09-mysql84-innodb-catalog-v1");
        Append(hash, basic.Version);
        Append(hash, schema);
        Append(hash, table);
        Append(hash, definition);
        return new SqlSchemaSnapshot(SqlAgentToolType.MySQL,
            "native-mysql84-innodb-source-v1:"
            + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            basic.Tables);
    }

    private static DbCommand Command(DbConnection connection, DbTransaction? transaction,
        string sql, string schema, string table)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        var p = cmd.CreateParameter();
        p.ParameterName = "@schema";
        p.Value = schema;
        cmd.Parameters.Add(p);
        p = cmd.CreateParameter();
        p.ParameterName = "@table";
        p.Value = table;
        cmd.Parameters.Add(p);
        return cmd;
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }
}
