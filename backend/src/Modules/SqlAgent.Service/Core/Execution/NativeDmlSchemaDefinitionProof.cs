using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HsSqlAgent.SqlCore.Core.Pipeline;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// Additional opt-in catalog attestation for Oracle and Firebird. The query
/// arrays are intentionally provider-specific: metadata visibility and source
/// types differ. Reads use the preview connection or the SAME commit transaction.
/// A missing required definition or an unreadable BLOB fails closed.
/// </summary>
internal static class NativeDmlSchemaDefinitionProof
{
    public static async Task<SqlSchemaSnapshot?> ExtendAsync(
        DbConnection connection,
        DbTransaction? transaction,
        SqlSchemaSnapshot basic,
        string schema,
        string table,
        CancellationToken token)
    {
        var provider = basic.Provider;
        if (provider is not (SqlAgentToolType.Oracle or SqlAgentToolType.Firebird))
            throw new ArgumentException("Unsupported definition-proof provider.", nameof(basic));

        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(digest, "native-definition-proof-v2");
        Append(digest, basic.Version);
        string[] queries;
        if (provider == SqlAgentToolType.Oracle)
        {
            // ALL_TAB_COLS includes hidden and generated columns suppressed by
            // ALL_TAB_COLUMNS; avoid DATA_DEFAULT (LONG) and unproven truncation.
            queries =
            [
                """
                SELECT c.COLUMN_NAME, c.DATA_TYPE, c.DATA_LENGTH,
                       c.DATA_PRECISION, c.DATA_SCALE, c.NULLABLE,
                       c.HIDDEN_COLUMN, c.VIRTUAL_COLUMN, c.IDENTITY_COLUMN,
                       c.DEFAULT_ON_NULL, c.DEFAULT_LENGTH, c.COLLATION
                FROM ALL_TAB_COLS c
                WHERE c.OWNER = :owner AND c.TABLE_NAME = :physicalTable
                ORDER BY c.INTERNAL_COLUMN_ID, c.COLUMN_NAME
                """,
                """
                SELECT c.CONSTRAINT_NAME, c.CONSTRAINT_TYPE, c.STATUS,
                       c.VALIDATED, c.DEFERRABLE, c.DEFERRED, c.DELETE_RULE,
                       c.R_OWNER, c.R_CONSTRAINT_NAME, c.SEARCH_CONDITION_VC,
                       cc.POSITION, cc.COLUMN_NAME
                FROM ALL_CONSTRAINTS c
                LEFT JOIN ALL_CONS_COLUMNS cc
                  ON cc.OWNER = c.OWNER AND cc.TABLE_NAME = c.TABLE_NAME
                 AND cc.CONSTRAINT_NAME = c.CONSTRAINT_NAME
                WHERE c.OWNER = :owner AND c.TABLE_NAME = :physicalTable
                ORDER BY c.CONSTRAINT_NAME, cc.POSITION
                """,
                """
                SELECT t.TRIGGER_NAME, t.STATUS, t.TRIGGER_TYPE,
                       t.TRIGGERING_EVENT, t.BASE_OBJECT_TYPE,
                       s.LINE, s.TEXT
                FROM ALL_TRIGGERS t
                LEFT JOIN ALL_SOURCE s
                  ON s.OWNER = t.OWNER AND s.NAME = t.TRIGGER_NAME
                 AND s.TYPE = 'TRIGGER'
                WHERE t.TABLE_OWNER = :owner AND t.TABLE_NAME = :physicalTable
                ORDER BY t.TRIGGER_NAME, s.LINE
                """
            ];
        }
        else
        {
            // BLOB TEXT source is fetched through provider-native ADO.NET,
            // never silently truncated via CAST(... AS VARCHAR).
            queries =
            [
                """
                SELECT TRIM(rf.RDB$FIELD_NAME), rf.RDB$FIELD_POSITION,
                       rf.RDB$NULL_FLAG, f.RDB$NULL_FLAG,
                       rf.RDB$DEFAULT_SOURCE, f.RDB$DEFAULT_SOURCE,
                       f.RDB$COMPUTED_SOURCE, rf.RDB$IDENTITY_TYPE,
                       rf.RDB$UPDATE_FLAG, f.RDB$FIELD_LENGTH,
                       f.RDB$FIELD_SCALE, f.RDB$FIELD_SUB_TYPE,
                       f.RDB$CHARACTER_SET_ID, rf.RDB$COLLATION_ID
                FROM RDB$RELATION_FIELDS rf
                JOIN RDB$FIELDS f ON f.RDB$FIELD_NAME = rf.RDB$FIELD_SOURCE
                WHERE rf.RDB$RELATION_NAME = @table
                ORDER BY rf.RDB$FIELD_POSITION, rf.RDB$FIELD_NAME
                """,
                """
                SELECT TRIM(c.RDB$CONSTRAINT_NAME), TRIM(c.RDB$CONSTRAINT_TYPE),
                       TRIM(c.RDB$INDEX_NAME),
                       TRIM(r.RDB$UPDATE_RULE), TRIM(r.RDB$DELETE_RULE)
                FROM RDB$RELATION_CONSTRAINTS c
                LEFT JOIN RDB$REF_CONSTRAINTS r
                  ON r.RDB$CONSTRAINT_NAME = c.RDB$CONSTRAINT_NAME
                WHERE c.RDB$RELATION_NAME = @table
                ORDER BY c.RDB$CONSTRAINT_NAME
                """,
                """
                SELECT TRIM(t.RDB$TRIGGER_NAME), t.RDB$TRIGGER_TYPE,
                       t.RDB$TRIGGER_SEQUENCE, t.RDB$TRIGGER_INACTIVE,
                       t.RDB$TRIGGER_SOURCE
                FROM RDB$TRIGGERS t
                WHERE t.RDB$RELATION_NAME = @table
                  AND COALESCE(t.RDB$SYSTEM_FLAG, 0) = 0
                ORDER BY t.RDB$TRIGGER_NAME
                """
            ];
        }

        for (var section = 0; section < queries.Length; section++)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = queries[section];
            if (provider == SqlAgentToolType.Oracle)
            {
                Add(command, "owner", schema);
                Add(command, "physicalTable", table);
            }
            else
            {
                Add(command, "table", table);
            }

            Append(digest, section.ToString(CultureInfo.InvariantCulture));
            var rows = 0;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                // The required textual definition must never be absent:
                // Oracle trigger source; Firebird user-trigger PSQL.
                if (provider == SqlAgentToolType.Oracle && section == 2
                    && reader.IsDBNull(6))
                    return null;
                if (provider == SqlAgentToolType.Firebird && section == 2
                    && reader.IsDBNull(4))
                    return null;
                // Oracle CHECK text is VARCHAR2(4000) and can truncate.
                // A 4k result cannot constitute an authoritative proof.
                if (provider == SqlAgentToolType.Oracle && section == 1
                    && !reader.IsDBNull(9)
                    && reader.GetString(9).Length >= 4000)
                    return null;

                Append(digest, "row");
                for (var col = 0; col < reader.FieldCount; col++)
                {
                    if (reader.IsDBNull(col))
                    {
                        Append(digest, "null");
                        continue;
                    }

                    var value = reader.GetValue(col);
                    // In particular, Firebird BLOB TEXT must be returned as a
                    // string, not an opaque stream/byte[] or a truncated value.
                    if (value is not (string or byte or short or int or long
                        or decimal or bool or float or double))
                        return null;

                    Append(digest, "value");
                    Append(digest, Convert.ToString(value, CultureInfo.InvariantCulture)!);
                }
                rows++;
            }

            if (section == 0 && rows == 0)
                return null;
            Append(digest, rows.ToString(CultureInfo.InvariantCulture));
        }

        var version = "native-dml-schema-v2:" +
            Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
        return new SqlSchemaSnapshot(provider, version, basic.Tables);
    }

    private static void Add(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var encoded = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(encoded.Length));
        hash.AppendData(encoded);
    }
}
