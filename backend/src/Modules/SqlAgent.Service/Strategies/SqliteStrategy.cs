using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;
using SqlKata.Compilers;
using System.Data.Common;
using System.Text.Json;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Interfaces;

namespace SqlAgent.Service.Strategies;

public class SqliteStrategy : BaseSqlStrategy
{
    public SqliteStrategy(IValidator validator, IQueryValueParserService valueParser)
        : base(validator, valueParser)
    {
    }

    public override SqlAgentToolType DbType => SqlAgentToolType.Sqlite;

    protected override DbConnection CreateConnection(string? connectionString) => new SqliteConnection(connectionString);
    protected override Compiler CreateCompiler() => new SqliteCompiler();

    public override async Task<List<string>> GetSchemasAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        // SQLite does not support multiple schemas in the same way as other RDBMS.
        // Returning an empty list or a list with a single default schema.
        return ["sqllite does not support schemas, please use get_tables to see available tables."];
    }

    public override async Task<List<string>> GetTablesAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            return [.. await connection.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type='table';")];
        }
        catch (Exception ex)
        {
            throw new Exception(@$"
                Error getting tables: {ex.Message},
                please try again !!
            ");
        }
    }

    public override async Task<List<string>> GetColumnsAsync(string connectionString, string tableName, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            var result = await connection.QueryAsync($"PRAGMA table_info([{tableName}])");
            return [.. result.Select(r => (string)r.name)];
        }
        catch (Exception ex)
        {
            throw new Exception(@$"
                Error getting columns: {ex.Message},
                please try again !!
            ");
        }
    }

    public override async Task<string> GetTableReferenceAsync(string connectionString, string tableName, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            var fkRows = await connection.QueryAsync($"PRAGMA foreign_key_list([{tableName}])");

            var result = new List<object>();
            foreach (var fk in fkRows)
            {
                var refTable = (string)fk.table;
                var pkColumns = await connection.QueryAsync($"PRAGMA table_info([{refTable}])");
                var refPk = pkColumns.FirstOrDefault(c => (long)c.pk > 0);

                result.Add(new
                {
                    SourceTable = tableName,
                    ReferenceTable = refTable,
                    PrimaryKey = refPk == null ? (string)fk.to : (string)refPk.name,
                    ForeignKey = (string)fk.from
                });
            }

            return JsonSerializer.Serialize(result);
        }
        catch (Exception ex)
        {
            throw new Exception(@$"
                Error getting table reference: {ex.Message},
                please try again !!
            ");
        }
    }
}