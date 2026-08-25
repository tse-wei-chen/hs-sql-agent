using System.Data.Common;
using SqlAgent.Service.Core.Providers;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class ProviderUniqueKeyMetadataNativeTests
{
    [Fact]
    public async Task Postgres_Inventory_PreservesSimplePartialAndExpressionUniqueKeys()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var provider = new PostgresProvider();
        var table = "uk_meta_" + Guid.NewGuid().ToString("N")[..12];
        var composite = "uq_tenant_email_" + table;
        var partial = "uq_partial_email_" + table;
        var expression = "uq_lower_name_" + table;
        using var connection = provider.CreateConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var schema = await ScalarStringAsync(connection, "SELECT current_schema()", TestContext.Current.CancellationToken);

        try
        {
            await ExecuteAsync(connection, $@"
                CREATE TABLE \"{table}\" (
                    id bigint PRIMARY KEY,
                    tenant_id bigint NOT NULL,
                    email text NOT NULL,
                    nickname text NULL
                );
                CREATE UNIQUE INDEX \"{composite}\" ON \"{table}\" (tenant_id, email);
                CREATE UNIQUE INDEX \"{partial}\" ON \"{table}\" (email) WHERE nickname IS NOT NULL;
                CREATE UNIQUE INDEX \"{expression}\" ON \"{table}\" ((lower(nickname)));",
                TestContext.Current.CancellationToken);

            var keys = await provider.GetUniqueKeysAsync(
                connectionString,
                schema,
                table,
                TestContext.Current.CancellationToken);

            var primaryKey = Assert.Single(keys, key => key.IsPrimaryKey);
            Assert.True(primaryKey.IsSimpleEnforcedColumnKey);
            Assert.Equal(["id"], primaryKey.Columns);

            var compositeKey = Assert.Single(keys, key => key.Name == composite);
            Assert.True(compositeKey.IsSimpleEnforcedColumnKey);
            Assert.Equal(["tenant_id", "email"], compositeKey.Columns);

            var partialKey = Assert.Single(keys, key => key.Name == partial);
            Assert.True(partialKey.IsPartial);
            Assert.False(partialKey.IsSimpleEnforcedColumnKey);

            var expressionKey = Assert.Single(keys, key => key.Name == expression);
            Assert.True(expressionKey.HasExpressions);
            Assert.False(expressionKey.IsSimpleEnforcedColumnKey);
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP TABLE IF EXISTS \"{table}\" CASCADE", TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task MySql_Inventory_PreservesPrimaryCompositeAndPrefixUniqueKeys()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_MYSQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var provider = new MySqlProvider();
        var table = "uk_meta_" + Guid.NewGuid().ToString("N")[..12];
        var composite = "uq_tenant_email";
        var prefix = "uq_nickname_prefix";
        using var connection = provider.CreateConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var schema = await ScalarStringAsync(connection, "SELECT DATABASE()", TestContext.Current.CancellationToken);

        try
        {
            await ExecuteAsync(connection, $@"
                CREATE TABLE `{table}` (
                    id bigint NOT NULL,
                    tenant_id bigint NOT NULL,
                    email varchar(255) NOT NULL,
                    nickname varchar(255) NULL,
                    PRIMARY KEY (id),
                    UNIQUE KEY `{composite}` (tenant_id, email),
                    UNIQUE KEY `{prefix}` (nickname(16))
                )",
                TestContext.Current.CancellationToken);

            var keys = await provider.GetUniqueKeysAsync(
                connectionString,
                schema,
                table,
                TestContext.Current.CancellationToken);

            var primaryKey = Assert.Single(keys, key => key.IsPrimaryKey);
            Assert.Equal("PRIMARY", primaryKey.Name, ignoreCase: true);
            Assert.True(primaryKey.IsSimpleEnforcedColumnKey);
            Assert.Equal(["id"], primaryKey.Columns);

            var compositeKey = Assert.Single(keys, key => key.Name == composite);
            Assert.True(compositeKey.IsSimpleEnforcedColumnKey);
            Assert.Equal(["tenant_id", "email"], compositeKey.Columns);

            var prefixKey = Assert.Single(keys, key => key.Name == prefix);
            Assert.True(prefixKey.HasPrefixKeyParts);
            Assert.False(prefixKey.IsSimpleEnforcedColumnKey);
            Assert.Equal(["nickname"], prefixKey.Columns);
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{table}`", TestContext.Current.CancellationToken);
        }
    }

    private static async Task ExecuteAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string> ScalarStringAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToString(value)
            ?? throw new InvalidOperationException($"Metadata setup query '{sql}' returned null.");
    }
}
