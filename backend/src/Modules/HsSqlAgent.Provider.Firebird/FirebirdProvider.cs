using System.Data.Common;
using Dapper;
using FirebirdSql.Data.FirebirdClient;
using HsSqlAgent.Provider.Abstractions;

namespace HsSqlAgent.Provider.Firebird;

public class FirebirdProvider : SqlProviderBase
{
    private readonly IDmlPreviewTransactionFactory _previewTransactions =
        new FirebirdDmlPreviewTransactionFactory();

    public override SqlAgentToolType DbType => SqlAgentToolType.Firebird;
    public override IDmlPreviewTransactionFactory PreviewTransactions => _previewTransactions;

    public override string BuildConnectionString(BuildDbConnectionModelBase model)
    {
        var builder = new FbConnectionStringBuilder
        {
            DataSource = model.Host,
            Port = string.IsNullOrEmpty(model.Port) ? 3050 : int.Parse(model.Port),
            UserID = model.Username,
            Password = model.Password,
            Database = model.Database
        };
        return builder.ConnectionString;
    }

    public override DbConnection CreateConnection(string? connectionString) => new FbConnection(connectionString);
    public override Task<List<string>> GetSchemasAsync(string connectionString, CancellationToken cancellationToken = default) => Task.FromResult(new List<string> { "Default" });

    public override async Task<List<string>> GetTablesAsync(string connectionString, string schemaName, CancellationToken cancellationToken = default)
    {
        using var connection = CreateConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = "SELECT TRIM(RDB$RELATION_NAME) FROM RDB$RELATIONS WHERE RDB$SYSTEM_FLAG = 0 AND RDB$VIEW_BLR IS NULL;";
        return [.. await connection.QueryAsync<string>(sql)];
    }

    public override async Task<IReadOnlyList<DatabaseTableMetadata>> FindTablesAsync(
        string connectionString,
        string tableName,
        CancellationToken cancellationToken = default)
    {
        using var connection = CreateConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = @"
            SELECT TRIM(RDB$RELATION_NAME) AS TABLE_NAME
            FROM RDB$RELATIONS
            WHERE RDB$SYSTEM_FLAG = 0
              AND RDB$VIEW_BLR IS NULL
              AND UPPER(TRIM(RDB$RELATION_NAME)) = UPPER(@tableName);";
        var tables = await connection.QueryAsync<string>(new CommandDefinition(
            sql,
            new { tableName },
            cancellationToken: cancellationToken));
        return [.. tables.Select(table => new DatabaseTableMetadata("Default", table))];
    }

    public override async Task<List<ColumnInfo>> GetColumnsAsync(string connectionString, string schemaName, string tableName, CancellationToken cancellationToken = default)
    {
        using var connection = CreateConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = @"
            SELECT TRIM(f.RDB$FIELD_NAME) AS COLUMN_NAME,
                   CASE t.RDB$TYPE_NAME
                       WHEN 'SHORT' THEN 'SMALLINT'
                       WHEN 'LONG' THEN 'INTEGER'
                       WHEN 'INT64' THEN 'BIGINT'
                       WHEN 'FLOAT' THEN 'FLOAT'
                       WHEN 'DOUBLE' THEN 'DOUBLE PRECISION'
                       WHEN 'VARYING' THEN 'VARCHAR'
                       WHEN 'TEXT' THEN 'CHAR'
                       WHEN 'BLOB' THEN 'BLOB'
                       WHEN 'TIMESTAMP' THEN 'TIMESTAMP'
                       WHEN 'SQL_DATE' THEN 'DATE'
                       WHEN 'SQL_TIME' THEN 'TIME'
                       WHEN 'BOOLEAN' THEN 'BOOLEAN'
                       ELSE TRIM(t.RDB$TYPE_NAME)
                   END AS DATA_TYPE,
                   CASE WHEN pk.RDB$FIELD_NAME IS NULL THEN 0 ELSE 1 END AS IS_PRIMARY_KEY,
                   CASE WHEN pk.RDB$FIELD_POSITION IS NULL THEN NULL ELSE pk.RDB$FIELD_POSITION + 1 END AS PRIMARY_KEY_ORDINAL
            FROM RDB$RELATION_FIELDS f
            JOIN RDB$FIELDS fs ON fs.RDB$FIELD_NAME = f.RDB$FIELD_SOURCE
            JOIN RDB$TYPES t ON t.RDB$TYPE = fs.RDB$FIELD_TYPE AND t.RDB$FIELD_NAME = 'RDB$FIELD_TYPE'
            LEFT JOIN (
                SELECT rc.RDB$RELATION_NAME, seg.RDB$FIELD_NAME, seg.RDB$FIELD_POSITION
                FROM RDB$RELATION_CONSTRAINTS rc
                JOIN RDB$INDEX_SEGMENTS seg ON seg.RDB$INDEX_NAME = rc.RDB$INDEX_NAME
                WHERE rc.RDB$CONSTRAINT_TYPE = 'PRIMARY KEY'
            ) pk ON pk.RDB$RELATION_NAME = f.RDB$RELATION_NAME AND pk.RDB$FIELD_NAME = f.RDB$FIELD_NAME
            WHERE f.RDB$RELATION_NAME = UPPER(@tableName)
            ORDER BY f.RDB$FIELD_POSITION;";
        var rows = await connection.QueryAsync(sql, new { tableName });
        return [.. rows.Select(r => new ColumnInfo(((string)r.COLUMN_NAME).TrimEnd(), ((string)r.DATA_TYPE).TrimEnd(),
            Convert.ToInt32(r.IS_PRIMARY_KEY) != 0,
            r.PRIMARY_KEY_ORDINAL is null ? null : Convert.ToInt32(r.PRIMARY_KEY_ORDINAL)))];
    }

    public override async Task<List<DatabaseUniqueKeyMetadata>> GetUniqueKeysAsync(string connectionString, string schemaName, string tableName, CancellationToken cancellationToken = default)
    {
        using var connection = CreateConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        const string partialCatalogSql = @"
            SELECT COUNT(*) FROM RDB$RELATION_FIELDS
            WHERE RDB$RELATION_NAME = 'RDB$INDICES' AND RDB$FIELD_NAME = 'RDB$CONDITION_SOURCE'";
        var supportsPartialIndexMetadata = await connection.ExecuteScalarAsync<int>(new CommandDefinition(partialCatalogSql, cancellationToken: cancellationToken)) > 0;
        var partialProjection = supportsPartialIndexMetadata ? "CASE WHEN i.RDB$CONDITION_SOURCE IS NULL THEN 0 ELSE 1 END" : "0";
        var sql = $@"
            SELECT TRIM(i.RDB$INDEX_NAME) AS INDEX_NAME,
                   CASE WHEN rc.RDB$CONSTRAINT_TYPE = 'PRIMARY KEY' THEN 1 ELSE 0 END AS IS_PRIMARY_KEY,
                   CASE WHEN COALESCE(i.RDB$INDEX_INACTIVE, 0) = 0 THEN 1 ELSE 0 END AS IS_ENFORCED,
                   {partialProjection} AS IS_PARTIAL,
                   CASE WHEN i.RDB$EXPRESSION_SOURCE IS NULL THEN 0 ELSE 1 END AS HAS_EXPRESSIONS,
                   CASE WHEN seg.RDB$FIELD_POSITION IS NULL THEN NULL ELSE seg.RDB$FIELD_POSITION + 1 END AS KEY_ORDINAL,
                   TRIM(seg.RDB$FIELD_NAME) AS COLUMN_NAME
            FROM RDB$INDICES i
            LEFT JOIN RDB$RELATION_CONSTRAINTS rc ON rc.RDB$INDEX_NAME = i.RDB$INDEX_NAME AND rc.RDB$CONSTRAINT_TYPE IN ('PRIMARY KEY', 'UNIQUE')
            LEFT JOIN RDB$INDEX_SEGMENTS seg ON seg.RDB$INDEX_NAME = i.RDB$INDEX_NAME
            WHERE i.RDB$RELATION_NAME = UPPER(@tableName) AND i.RDB$UNIQUE_FLAG = 1
            ORDER BY i.RDB$INDEX_NAME, seg.RDB$FIELD_POSITION";
        var rows = (await connection.QueryAsync(new CommandDefinition(sql, new { tableName }, cancellationToken: cancellationToken))).ToArray();
        return [.. rows.GroupBy(row => ((string)row.INDEX_NAME).TrimEnd(), StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            var ordered = group.OrderBy(row => row.KEY_ORDINAL is null ? int.MaxValue : Convert.ToInt32(row.KEY_ORDINAL)).ToArray();
            var first = ordered[0];
            return new DatabaseUniqueKeyMetadata(schemaName, tableName, group.Key,
                Convert.ToInt32(first.IS_PRIMARY_KEY) != 0,
                ordered.Where(row => row.COLUMN_NAME is not null).Select(row => ((string)row.COLUMN_NAME).TrimEnd()).ToArray(),
                IsPartial: Convert.ToInt32(first.IS_PARTIAL) != 0,
                HasExpressions: Convert.ToInt32(first.HAS_EXPRESSIONS) != 0 || ordered.Any(row => row.COLUMN_NAME is null),
                IsEnforced: Convert.ToInt32(first.IS_ENFORCED) != 0);
        })];
    }
}
