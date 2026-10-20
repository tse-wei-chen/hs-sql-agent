using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Microsoft.Data.Sqlite;
using MySql.Data.MySqlClient;
using Npgsql;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Real transaction-bound INSERT/UPDATE/DELETE parity for all nine ordered
/// PostgreSQL, MySQL and SQLite source/target combinations.
/// Every execution is rolled back; no persistent test records are created.
/// </summary>
public sealed class SqlCoreThreeProviderDmlExecutionParityTests
{
    private const string Table = "sqlcore_parity_mutations";
    private const string OriginalState =
        "[[\"1\",\"Ada\"],[\"2\",\"Bea\"],[\"3\",null],[\"4\",\"Dana\"]]";

    private static readonly SqlAgentToolType[] Providers =
    [
        SqlAgentToolType.Postgres,
        SqlAgentToolType.MySQL,
        SqlAgentToolType.Sqlite
    ];

    private sealed record MutationCase(
        string Sql, int AffectedRows, string ExpectedState);

    private static readonly MutationCase[] Mutations =
    [
        new(
            "UPDATE sqlcore_parity_mutations SET name = 'Changed' WHERE id = 2",
            1,
            "[[\"1\",\"Ada\"],[\"2\",\"Changed\"],[\"3\",null],[\"4\",\"Dana\"]]"),
        new(
            "UPDATE sqlcore_parity_mutations SET name = NULL WHERE id = 2",
            1,
            "[[\"1\",\"Ada\"],[\"2\",null],[\"3\",null],[\"4\",\"Dana\"]]"),
        new(
            "DELETE FROM sqlcore_parity_mutations WHERE id = 3",
            1,
            "[[\"1\",\"Ada\"],[\"2\",\"Bea\"],[\"4\",\"Dana\"]]"),
        new(
            "INSERT INTO sqlcore_parity_mutations (id, name) VALUES (5, 'Eve')",
            1,
            "[[\"1\",\"Ada\"],[\"2\",\"Bea\"],[\"3\",null],[\"4\",\"Dana\"],[\"5\",\"Eve\"]]"),
        new(
            "DELETE FROM sqlcore_parity_mutations WHERE id = 99",
            0,
            OriginalState)
    ];

    [Fact]
    public async Task DmlNativeAndCrossDialect_ExecuteEqualAffectedRowsAndMutationStates()
    {
        var postgresConnection = RequiredConnection("TEST_POSTGRES_CONNECTION");
        var mysqlConnection = RequiredConnection("TEST_MYSQL_CONNECTION");
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var postgres = new NpgsqlConnection(postgresConnection);
        await using var mysql = new MySqlConnection(mysqlConnection);
        await using var sqlite = new SqliteConnection("Data Source=:memory:");

        await postgres.OpenAsync(cancellationToken);
        await mysql.OpenAsync(cancellationToken);
        await sqlite.OpenAsync(cancellationToken);

        await InitializeAsync(postgres,
            $"CREATE TEMP TABLE {Table} (id INTEGER PRIMARY KEY, name TEXT NULL)",
            cancellationToken);
        await InitializeAsync(mysql,
            $"CREATE TEMPORARY TABLE {Table} (id INT PRIMARY KEY, name TEXT NULL) ENGINE=InnoDB",
            cancellationToken);
        await InitializeAsync(sqlite,
            $"CREATE TABLE {Table} (id INTEGER PRIMARY KEY, name TEXT NULL)",
            cancellationToken);

        DbConnection Connection(SqlAgentToolType provider) => provider switch
        {
            SqlAgentToolType.Postgres => postgres,
            SqlAgentToolType.MySQL => mysql,
            SqlAgentToolType.Sqlite => sqlite,
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };

        var assertions = 0;
        foreach (var mutation in Mutations)
        {
            foreach (var source in Providers)
            {
                foreach (var target in Providers)
                {
                    var connection = Connection(target);
                    var compiled = Compile(mutation.Sql, source, target);
                    await using (var transaction =
                        await connection.BeginTransactionAsync(cancellationToken))
                    {
                        var affected = await ExecuteCompiledAsync(
                            connection, transaction, compiled, cancellationToken);
                        Assert.Equal(mutation.AffectedRows, affected);

                        var after = await SnapshotAsync(
                            connection, transaction, cancellationToken);
                        Assert.Equal(mutation.ExpectedState, after);
                        // Explicit rollback is a tested part of the semantic contract.
                        await transaction.RollbackAsync(cancellationToken);
                    }

                    Assert.Equal(OriginalState,
                        await SnapshotAsync(connection, null, cancellationToken));
                    assertions++;
                }
            }
        }

        Assert.Equal(5, Mutations.Length);
        Assert.Equal(45, assertions);
    }

    [Fact]
    public async Task MultiStatementTransactions_DuplicateKeyFailureRollsBackAllProviders()
    {
        var postgresConnection = RequiredConnection("TEST_POSTGRES_CONNECTION");
        var mysqlConnection = RequiredConnection("TEST_MYSQL_CONNECTION");
        var token = TestContext.Current.CancellationToken;

        await using var postgres = new NpgsqlConnection(postgresConnection);
        await using var mysql = new MySqlConnection(mysqlConnection);
        await using var sqlite = new SqliteConnection("Data Source=:memory:");

        await postgres.OpenAsync(token);
        await mysql.OpenAsync(token);
        await sqlite.OpenAsync(token);

        await InitializeAsync(postgres,
            $"CREATE TEMP TABLE {Table} (id INTEGER PRIMARY KEY, name TEXT NULL)", token);
        await InitializeAsync(mysql,
            $"CREATE TEMPORARY TABLE {Table} (id INT PRIMARY KEY, name TEXT NULL) ENGINE=InnoDB", token);
        await InitializeAsync(sqlite,
            $"CREATE TABLE {Table} (id INTEGER PRIMARY KEY, name TEXT NULL)", token);

        var connections = new (SqlAgentToolType Provider, DbConnection Connection)[]
        {
            (SqlAgentToolType.Postgres, postgres),
            (SqlAgentToolType.MySQL, mysql),
            (SqlAgentToolType.Sqlite, sqlite)
        };

        foreach (var (provider, connection) in connections)
        {
            await using (var transaction = await connection.BeginTransactionAsync(token))
            {
                var first = Compile(
                    $"UPDATE {Table} SET name = 'Pending' WHERE id = 2",
                    provider, provider);
                var second = Compile(
                    $"INSERT INTO {Table} (id, name) VALUES (1, 'Duplicate')",
                    provider, provider);

                Assert.Equal(1,
                    await ExecuteCompiledAsync(connection, transaction, first, token));
                await Assert.ThrowsAnyAsync<DbException>(() =>
                    ExecuteCompiledAsync(connection, transaction, second, token));

                await transaction.RollbackAsync(token);
            }

            Assert.Equal(OriginalState, await SnapshotAsync(connection, null, token));
        }
    }

    private static string RequiredConnection(string environment)
    {
        var value = Environment.GetEnvironmentVariable(environment);
        if (!string.IsNullOrWhiteSpace(value)) return value;

        if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{environment} must be configured for database execution parity in CI.");
        }

        Assert.Skip($"{environment} is required for real PostgreSQL/MySQL execution.");
        throw new InvalidOperationException("Unreachable after Assert.Skip.");
    }

    private static CompiledSqlCommand Compile(
        string sql, SqlAgentToolType source, SqlAgentToolType target)
    {
        var schema = new SqlSchemaSnapshot(source, "parity-dml-v1",
        [
            SqlSchemaTable.WithTypes(Table,
            [
                new SqlSchemaColumn("id", SqlSchemaValueKind.Numeric),
                new SqlSchemaColumn("name", SqlSchemaValueKind.Text)
            ])
        ]);
        return SqlCoreFacade.CompileDml(
            sql, source, target,
            new SqlPlanValidationContext("parity-dml-policy-v1").WithSchemaSnapshot(schema));
    }

    private static async Task InitializeAsync(
        DbConnection connection, string createSql, CancellationToken token)
    {
        await using var create = connection.CreateCommand();
        create.CommandText = createSql;
        await create.ExecuteNonQueryAsync(token);

        await using var seed = connection.CreateCommand();
        seed.CommandText =
            $"INSERT INTO {Table} (id, name) VALUES " +
            "(1, 'Ada'), (2, 'Bea'), (3, NULL), (4, 'Dana')";
        await seed.ExecuteNonQueryAsync(token);

        Assert.Equal(OriginalState, await SnapshotAsync(connection, null, token));
    }

    private static async Task<int> ExecuteCompiledAsync(
        DbConnection connection,
        DbTransaction transaction,
        CompiledSqlCommand compiled,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = compiled.Sql;
        foreach (var binding in compiled.Parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = binding.Name;
            parameter.Value = binding.Value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        return await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<string> SnapshotAsync(
        DbConnection connection, DbTransaction? transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT id, name FROM {Table} ORDER BY id";
        await using var reader = await command.ExecuteReaderAsync(token);
        var rows = new List<string?[]>();
        while (await reader.ReadAsync(token))
        {
            rows.Add(
            [
                Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture),
                reader.IsDBNull(1)
                    ? null
                    : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture)
            ]);
        }
        return JsonSerializer.Serialize(rows);
    }
}
