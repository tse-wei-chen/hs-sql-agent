using System.Collections.Immutable;
using System.Data.Common;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using Microsoft.Data.Sqlite;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Real SQLite UPDATE FROM parity for a structurally singleton read-only CTE.
/// Compare RETURNING, source-dependent assignments and all remaining rows.
/// </summary>
public sealed class SqlCoreSqliteSingletonCteUpdateFromLiveParityTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SingleSourceCteUpdateFromMatchesNativeSQLite(int cteShape)
    {
        var token = TestContext.Current.CancellationToken;
        await using var db = new SqliteConnection("Data Source=:memory:");
        await db.OpenAsync(token);
        await Execute(db,
            "CREATE TABLE native_result(id INTEGER PRIMARY KEY, name TEXT);" +
            "CREATE TABLE compiled_result(id INTEGER PRIMARY KEY, name TEXT);" +
            "INSERT INTO native_result(id,name) VALUES(1,'old'),(2,'old'),(3,'old');" +
            "INSERT INTO compiled_result(id,name) VALUES(1,'old'),(2,'old'),(3,'old');",
            Array.Empty<SqlParameterValue>(), token);

        var with = cteShape switch
        {
            0 => "WITH picked AS (SELECT 2 AS id, 'changed' AS label) ",
            1 => "WITH seed AS (SELECT 2 AS id, 'changed' AS label), " +
                 "picked AS (SELECT id, label FROM seed) ",
            _ => "WITH picked(id,label) AS (SELECT 2, 'changed') "
        };
        string Statement(string target) => with + "UPDATE " + target +
            " SET name = picked.label FROM picked WHERE " + target +
            ".id >= picked.id RETURNING id";

        var expected = await ReadIds(db, Statement("native_result"),
            Array.Empty<SqlParameterValue>(), token);
        var profile = new SqlProviderCapabilityProfile(SqlAgentToolType.Sqlite,
            Version.Parse(db.ServerVersion));
        var compiled = SqlCoreFacade.CompileDml(Statement("compiled_result"),
            SqlAgentToolType.Sqlite, SqlAgentToolType.Sqlite,
            new SqlPlanValidationContext("singleton-cte-update-from-live",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "compiled_result" }),
            new DmlCompilationPolicy(), profile, profile,
            new DmlConflictTargetAssurance(ImmutableArray<string>.Empty));

        Assert.Equal(SqlStatementKind.Update, compiled.Kind);
        Assert.StartsWith("WITH ", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" FROM ", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(compiled.Parameters, p => Equals(p.Value, 2));
        Assert.Contains(compiled.Parameters, p => Equals(p.Value, "changed"));

        var actual = await ReadIds(db, compiled.Sql, compiled.Parameters, token);
        Assert.Equal(new[] { 2, 3 }, expected.OrderBy(x => x));
        Assert.Equal(expected.OrderBy(x => x), actual.OrderBy(x => x));

        var nativeRows = await ReadRows(db, "SELECT id,name FROM native_result ORDER BY id", token);
        var compiledRows = await ReadRows(db, "SELECT id,name FROM compiled_result ORDER BY id", token);
        Assert.Equal(new[] { "1:old", "2:changed", "3:changed" }, nativeRows);
        Assert.Equal(nativeRows, compiledRows);
    }

    private static async Task Execute(DbConnection connection, string sql,
        IEnumerable<SqlParameterValue> parameters, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        Bind(command, parameters);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<int[]> ReadIds(DbConnection connection, string sql,
        IEnumerable<SqlParameterValue> parameters, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        Bind(command, parameters);
        var ids = new List<int>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) ids.Add(reader.GetInt32(0));
        return ids.ToArray();
    }

    private static async Task<string[]> ReadRows(DbConnection connection, string sql, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            values.Add(reader.GetInt32(0) + ":" + reader.GetString(1));
        return values.ToArray();
    }

    private static void Bind(DbCommand command, IEnumerable<SqlParameterValue> parameters)
    {
        foreach (var binding in parameters)
        {
            var p = command.CreateParameter();
            p.ParameterName = binding.Name;
            p.Value = binding.Value ?? DBNull.Value;
            command.Parameters.Add(p);
        }
    }
}
