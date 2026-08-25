using System.Data.Common;
using Dapper;
using Oracle.ManagedDataAccess.Client;
using HsSqlAgent.Provider.Abstractions;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;

namespace HsSqlAgent.Provider.Oracle;

public class OracleProvider : SqlProviderBase
{
    public override SqlAgentToolType DbType => SqlAgentToolType.Oracle;

    public override string BuildConnectionString(BuildDbConnectionModelBase model)
    {
        var builder = new OracleConnectionStringBuilder
        {
            DataSource = $"{model.Host}:{(string.IsNullOrEmpty(model.Port) ? "1521" : model.Port)}/{model.Database}",
            UserID = model.Username,
            Password = model.Password
        };
        return builder.ConnectionString;
    }

    public override DbConnection CreateConnection(string? connectionString) => new OracleConnection(connectionString);

    public override async Task<List<string>> GetSchemasAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        using var connection = CreateConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return [.. await connection.QueryAsync<string>("SELECT USERNAME FROM ALL_USERS ORDER BY USERNAME")];
    }

    public override async Task<List<string>> GetTablesAsync(string connectionString, string schemaName, CancellationToken cancellationToken = default)
    {
        using var connection = CreateConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = "SELECT TABLE_NAME FROM ALL_TABLES WHERE OWNER = :schemaName ORDER BY TABLE_NAME";
        return [.. await connection.QueryAsync<string>(sql, new { schemaName = schemaName.ToUpperInvariant() })];
    }

    public override async Task<List<ColumnInfo>> GetColumnsAsync(string connectionString, string schemaName, string tableName, CancellationToken cancellationToken = default)
    {
        using var connection = CreateConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = @"
            SELECT c.COLUMN_NAME, c.DATA_TYPE,
                   CASE WHEN pk.POSITION IS NULL THEN 0 ELSE 1 END AS IS_PRIMARY_KEY,
                   pk.POSITION AS PRIMARY_KEY_ORDINAL
            FROM ALL_TAB_COLUMNS c
            LEFT JOIN (
                SELECT acc.OWNER, acc.TABLE_NAME, acc.COLUMN_NAME, acc.POSITION
                FROM ALL_CONSTRAINTS ac
                JOIN ALL_CONS_COLUMNS acc
                  ON acc.OWNER = ac.OWNER
                 AND acc.CONSTRAINT_NAME = ac.CONSTRAINT_NAME
                 AND acc.TABLE_NAME = ac.TABLE_NAME
                WHERE ac.CONSTRAINT_TYPE = 'P'
            ) pk ON pk.OWNER = c.OWNER AND pk.TABLE_NAME = c.TABLE_NAME AND pk.COLUMN_NAME = c.COLUMN_NAME
            WHERE c.OWNER = :schemaName AND c.TABLE_NAME = :tableName
            ORDER BY c.COLUMN_ID";
        var rows = await connection.QueryAsync(sql, new { schemaName = schemaName.ToUpperInvariant(), tableName = tableName.ToUpperInvariant() });
        return [.. rows.Select(r => new ColumnInfo((string)r.COLUMN_NAME, (string)r.DATA_TYPE,
            Convert.ToInt32(r.IS_PRIMARY_KEY) != 0,
            r.PRIMARY_KEY_ORDINAL is null ? null : Convert.ToInt32(r.PRIMARY_KEY_ORDINAL)))];
    }

    public override async Task<List<DatabaseUniqueKeyMetadata>> GetUniqueKeysAsync(string connectionString, string schemaName, string tableName, CancellationToken cancellationToken = default)
    {
        using var connection = CreateConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = @"
            SELECT ac.CONSTRAINT_NAME AS KEY_NAME,
                   CASE WHEN ac.CONSTRAINT_TYPE = 'P' THEN 1 ELSE 0 END AS IS_PRIMARY_KEY,
                   CASE WHEN ac.STATUS = 'ENABLED' AND ac.VALIDATED = 'VALIDATED' THEN 1 ELSE 0 END AS IS_ENFORCED,
                   0 AS HAS_EXPRESSIONS, acc.POSITION AS KEY_ORDINAL, acc.COLUMN_NAME AS COLUMN_NAME
            FROM ALL_CONSTRAINTS ac
            JOIN ALL_CONS_COLUMNS acc
              ON acc.OWNER = ac.OWNER AND acc.CONSTRAINT_NAME = ac.CONSTRAINT_NAME AND acc.TABLE_NAME = ac.TABLE_NAME
            WHERE ac.OWNER = :schemaName AND ac.TABLE_NAME = :tableName AND ac.CONSTRAINT_TYPE IN ('P', 'U')
            UNION ALL
            SELECT i.INDEX_NAME AS KEY_NAME, 0 AS IS_PRIMARY_KEY,
                   CASE WHEN i.STATUS = 'VALID' THEN 1 ELSE 0 END AS IS_ENFORCED,
                   CASE WHEN i.INDEX_TYPE LIKE 'FUNCTION-BASED%' THEN 1 ELSE 0 END AS HAS_EXPRESSIONS,
                   ic.COLUMN_POSITION AS KEY_ORDINAL, ic.COLUMN_NAME AS COLUMN_NAME
            FROM ALL_INDEXES i
            JOIN ALL_IND_COLUMNS ic
              ON ic.INDEX_OWNER = i.OWNER AND ic.INDEX_NAME = i.INDEX_NAME
             AND ic.TABLE_OWNER = i.TABLE_OWNER AND ic.TABLE_NAME = i.TABLE_NAME
            WHERE i.TABLE_OWNER = :schemaName AND i.TABLE_NAME = :tableName AND i.UNIQUENESS = 'UNIQUE'
              AND NOT EXISTS (
                  SELECT 1 FROM ALL_CONSTRAINTS ac
                  WHERE ac.OWNER = i.TABLE_OWNER AND ac.TABLE_NAME = i.TABLE_NAME
                    AND ac.INDEX_NAME = i.INDEX_NAME AND ac.CONSTRAINT_TYPE IN ('P', 'U'))
            ORDER BY KEY_NAME, KEY_ORDINAL";
        var rows = (await connection.QueryAsync(new CommandDefinition(sql,
            new { schemaName = schemaName.ToUpperInvariant(), tableName = tableName.ToUpperInvariant() },
            cancellationToken: cancellationToken))).ToArray();
        return [.. rows.GroupBy(row => ((string)row.KEY_NAME, Convert.ToInt32(row.IS_PRIMARY_KEY) != 0)).Select(group =>
        {
            var ordered = group.OrderBy(row => Convert.ToInt32(row.KEY_ORDINAL)).ToArray();
            var first = ordered[0];
            return new DatabaseUniqueKeyMetadata(schemaName, tableName, group.Key.Item1, group.Key.Item2,
                ordered.Where(row => row.COLUMN_NAME is not null).Select(row => (string)row.COLUMN_NAME).ToArray(),
                HasExpressions: ordered.Any(row => Convert.ToInt32(row.HAS_EXPRESSIONS) != 0 || row.COLUMN_NAME is null),
                IsEnforced: Convert.ToInt32(first.IS_ENFORCED) != 0);
        })];
    }
}
