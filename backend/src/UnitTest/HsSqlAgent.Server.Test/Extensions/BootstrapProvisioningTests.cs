using System.Text;
using Admin.Service.Data;
using Admin.Service.Models;
using Common.Interfaces;
using Common.Services;
using System.Security.Cryptography;
using HsSqlAgent.Server.Extensions;
using HsSqlAgent.Server.Models;
using HsSqlAgent.SqliteMigrations;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace HsSqlAgent.Server.Test.Extensions;

public class BootstrapProvisioningTests
{
    [Fact]
    public async Task UseHsSqlAgent_ShouldProvisionDbAndKey_WhenBootstrapEnabled()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var adminDbOptions = new DbContextOptionsBuilder<AdminContext>()
            .UseSqlite(connection, sqlite =>
                sqlite.MigrationsAssembly(typeof(SqliteAdminContextFactory).Assembly.FullName))
            .Options;
        var authDbOptions = new DbContextOptionsBuilder<Auth.Service.Data.AuthContext>()
            .UseSqlite(connection, sqlite =>
                sqlite.MigrationsAssembly(typeof(SqliteAuthContextFactory).Assembly.FullName))
            .Options;

        var hmacKey = "test-hmac-key-that-is-at-least-32-bytes";
        var services = new ServiceCollection();
        V3TestEncryption.AddTo(services);

        services.AddAuthorization();
        services.AddAuthentication();
        services.AddScoped(_ => new AdminContext(adminDbOptions));
        services.AddScoped(_ => new Auth.Service.Data.AuthContext(authDbOptions));
        services.AddSingleton<ICryptoService, CryptoService>();
        services.AddSingleton<Admin.Service.Interfaces.ISecurityPolicyRuntimeState, Admin.Service.Services.SecurityPolicyRuntimeState>();
        services.AddSingleton(Options.Create(new McpKeySettings { HmacSecretKey = hmacKey }));
        var bootstrap = new BootstrapOptions
        {
            Enabled = true,
            Databases = [new BootstrapDatabaseOptions
            {
                BootstrapId = "test-db",
                Name = "Test Postgres",
                Provider = "Postgres",
                Host = "localhost",
                Port = "5432",
                Database = "testdb",
                Username = "testuser",
                Password = "plain-password",
                ExtraSettings = "",
                McpKeys = [new BootstrapMcpKeyOptions
                {
                    BootstrapId = "test-key",
                    Name = "Test Key",
                    Key = "bootstrap-mcp-key-123456",
                    AllowedTools = "execute_query_sql"
                }]
            }]
        };
        services.AddSingleton(Options.Create(bootstrap));

        var appBuilder = new ApplicationBuilder(services.BuildServiceProvider());
        appBuilder.UseHsSqlAgent();

        await using var verifyContext = new AdminContext(adminDbOptions);
        var db = await verifyContext.DbManagement.FirstOrDefaultAsync(x => x.Name == "Test Postgres", TestContext.Current.CancellationToken);
        Assert.NotNull(db);
        Assert.Equal("Postgres", db.SqlProvider);
        Assert.Equal("test-db", db.BootstrapId);
        Assert.Equal("localhost", db.Host);
        Assert.Equal("5432", db.Port);
        Assert.Equal("testdb", db.Database);
        Assert.Equal("testuser", db.Username);
        Assert.NotNull(db.PasswordHash);
        Assert.NotEqual("plain-password", db.PasswordHash);

        var crypto = new CryptoService();
        var decrypted = crypto.DecryptText(db.PasswordHash, Encoding.UTF8.GetBytes(hmacKey));
        Assert.Equal("plain-password", decrypted);

        var key = await verifyContext.McpAccessKeys.FirstOrDefaultAsync(x => x.Name == "Test Key", TestContext.Current.CancellationToken);
        Assert.NotNull(key);
        Assert.Equal("test-key", key.BootstrapId);
        Assert.Equal("bootstra", key.KeyPrefix);
        Assert.Equal(db.Id, key.DbManagementId);
        Assert.Equal("execute_query_sql", key.AllowedTools);

        var expectedKeyHash = McpAccessKeyCacheKeys.ComputeKeyHash("bootstrap-mcp-key-123456", Encoding.UTF8.GetBytes(hmacKey));
        Assert.Equal(expectedKeyHash, key.KeyHash);
    }

    [Fact]
    public async Task Bootstrap_NewDatabaseV2Password_IsCommittedWithMatchingIdAad()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var adminDbOptions = new DbContextOptionsBuilder<AdminContext>()
            .UseSqlite(connection, sqlite =>
                sqlite.MigrationsAssembly(typeof(SqliteAdminContextFactory).Assembly.FullName))
            .Options;
        var authDbOptions = new DbContextOptionsBuilder<Auth.Service.Data.AuthContext>()
            .UseSqlite(connection, sqlite =>
                sqlite.MigrationsAssembly(typeof(SqliteAuthContextFactory).Assembly.FullName))
            .Options;
        var hmacKey = "test-hmac-key-that-is-at-least-32-bytes";
        var protector = new VersionedSecretProtector(new SecretKeyRing(
            new Dictionary<SecretPurpose, SecretKeySet>
            {
                [SecretPurpose.DatabasePassword] = new SecretKeySet
                {
                    ActiveKeyId = "db_boot_1",
                    Keys = new Dictionary<string, byte[]>
                    {
                        ["db_boot_1"] = Enumerable.Repeat((byte)0x76, 32).ToArray()
                    }
                }
            }), Encoding.UTF8.GetBytes(hmacKey));

        var services = new ServiceCollection();
        V3TestEncryption.AddTo(services);
        services.AddAuthorization();
        services.AddAuthentication();
        services.AddScoped(_ => new AdminContext(adminDbOptions));
        services.AddScoped(_ => new Auth.Service.Data.AuthContext(authDbOptions));
        services.AddSingleton<ICryptoService, CryptoService>();
        services.AddSingleton<ISecretProtector>(protector);
        services.AddSingleton<ISecretWriteRolloutPolicy>(new TestDbPasswordWritePolicy());
        services.AddSingleton<Admin.Service.Interfaces.ISecurityPolicyRuntimeState,
            Admin.Service.Services.SecurityPolicyRuntimeState>();
        services.AddSingleton(Options.Create(new McpKeySettings { HmacSecretKey = hmacKey }));
        services.AddSingleton(Options.Create(new BootstrapOptions
        {
            Enabled = true,
            Databases = [new BootstrapDatabaseOptions
            {
                BootstrapId = "new-db",
                Name = "Primary",
                Provider = "Postgres",
                Host = "localhost",
                Password = "my-private-password",
                McpKeys = [new BootstrapMcpKeyOptions
                {
                    BootstrapId = "new-mcp-key",
                    Key = "explicit-mcp-key-value"
                }]
            }]
        }));
        using var provider = services.BuildServiceProvider();
        var appBuilder = new ApplicationBuilder(provider);
        appBuilder.UseHsSqlAgent();

        await using var verify = new AdminContext(adminDbOptions);
        var db = await verify.DbManagement.SingleAsync(TestContext.Current.CancellationToken);
        Assert.True(db.Id > 0);
        Assert.StartsWith("v2:db_boot_1:", db.PasswordHash);
        Assert.Equal("my-private-password", protector.Unprotect(
            db.PasswordHash, SecretPurpose.DatabasePassword,
            db.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(
            db.PasswordHash, SecretPurpose.DatabasePassword,
            (db.Id + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Single(await verify.McpAccessKeys.ToListAsync(TestContext.Current.CancellationToken));
    }

    private sealed class TestDbPasswordWritePolicy : ISecretWriteRolloutPolicy
    {
        public bool EnableV2DatabasePasswordWrites => true;
        public bool EnableV2DurableDmlWrites => false;
    }

    [Fact]
    public async Task UseHsSqlAgent_ShouldNotDuplicate_OnSecondRun()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var adminDbOptions = new DbContextOptionsBuilder<AdminContext>()
            .UseSqlite(connection, sqlite =>
                sqlite.MigrationsAssembly(typeof(SqliteAdminContextFactory).Assembly.FullName))
            .Options;
        var authDbOptions = new DbContextOptionsBuilder<Auth.Service.Data.AuthContext>()
            .UseSqlite(connection, sqlite =>
                sqlite.MigrationsAssembly(typeof(SqliteAuthContextFactory).Assembly.FullName))
            .Options;

        var hmacKey = "test-hmac-key-that-is-at-least-32-bytes";
        var services = new ServiceCollection();
        V3TestEncryption.AddTo(services);

        services.AddAuthorization();
        services.AddAuthentication();
        services.AddScoped(_ => new AdminContext(adminDbOptions));
        services.AddScoped(_ => new Auth.Service.Data.AuthContext(authDbOptions));
        services.AddSingleton<ICryptoService, CryptoService>();
        services.AddSingleton<Admin.Service.Interfaces.ISecurityPolicyRuntimeState, Admin.Service.Services.SecurityPolicyRuntimeState>();
        services.AddSingleton(Options.Create(new McpKeySettings { HmacSecretKey = hmacKey }));
        var bootstrap = new BootstrapOptions
        {
            Enabled = true,
            Databases = [new BootstrapDatabaseOptions
            {
                BootstrapId = "default-db",
                Name = "Default DB",
                Provider = "Postgres",
                Host = "localhost",
                Password = "pwd",
                McpKeys = [new BootstrapMcpKeyOptions
                {
                    BootstrapId = "default-key",
                    Name = "Default Key",
                    Key = "some-key"
                }]
            }]
        };
        services.AddSingleton(Options.Create(bootstrap));

        var appBuilder = new ApplicationBuilder(services.BuildServiceProvider());
        appBuilder.UseHsSqlAgent();
        bootstrap.Databases[0].Name = "Renamed DB";
        bootstrap.Databases[0].Host = "db.internal";
        bootstrap.Databases[0].McpKeys[0].Name = "Renamed Key";
        bootstrap.Databases[0].McpKeys[0].AllowedTools = "get_tables";
        bootstrap.Databases[0].McpKeys[0].Key = "updated-key";
        appBuilder.UseHsSqlAgent();
        // Omitting Key on subsequent reconciliation must preserve the stored hash.
        bootstrap.Databases[0].McpKeys[0].Key = null;
        appBuilder.UseHsSqlAgent();

        await using var verifyContext = new AdminContext(adminDbOptions);
        var dbCount = await verifyContext.DbManagement.CountAsync(TestContext.Current.CancellationToken);
        var keyCount = await verifyContext.McpAccessKeys.CountAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, dbCount);
        Assert.Equal(1, keyCount);
        var db = await verifyContext.DbManagement.SingleAsync(TestContext.Current.CancellationToken);
        var key = await verifyContext.McpAccessKeys.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Renamed DB", db.Name);
        Assert.Equal("db.internal", db.Host);
        Assert.Equal("Renamed Key", key.Name);
        Assert.Equal("get_tables", key.AllowedTools);
        Assert.Equal("updated-", key.KeyPrefix);
        Assert.Equal(
            McpAccessKeyCacheKeys.ComputeKeyHash("updated-key", Encoding.UTF8.GetBytes(hmacKey)),
            key.KeyHash);
    }

    [Fact]
    public async Task Bootstrap_MissingKeyOnFirstProvisioning_FailsBeforeWritingDatabase()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var adminDbOptions = new DbContextOptionsBuilder<AdminContext>()
            .UseSqlite(connection, sqlite =>
                sqlite.MigrationsAssembly(typeof(SqliteAdminContextFactory).Assembly.FullName))
            .Options;
        var authDbOptions = new DbContextOptionsBuilder<Auth.Service.Data.AuthContext>()
            .UseSqlite(connection, sqlite =>
                sqlite.MigrationsAssembly(typeof(SqliteAuthContextFactory).Assembly.FullName))
            .Options;

        var services = new ServiceCollection();
        V3TestEncryption.AddTo(services);
        services.AddAuthorization();
        services.AddAuthentication();
        services.AddScoped(_ => new AdminContext(adminDbOptions));
        services.AddScoped(_ => new Auth.Service.Data.AuthContext(authDbOptions));
        services.AddSingleton<ICryptoService, CryptoService>();
        services.AddSingleton<Admin.Service.Interfaces.ISecurityPolicyRuntimeState,
            Admin.Service.Services.SecurityPolicyRuntimeState>();
        services.AddSingleton(Options.Create(new McpKeySettings
        {
            HmacSecretKey = "test-hmac-key-that-is-at-least-32-bytes"
        }));
        services.AddSingleton(Options.Create(new BootstrapOptions
        {
            Enabled = true,
            Databases = [new BootstrapDatabaseOptions
            {
                BootstrapId = "db-1",
                Provider = "Postgres",
                Host = "localhost",
                McpKeys = [new BootstrapMcpKeyOptions
                {
                    BootstrapId = "key-1",
                    Name = "Cannot leak",
                    Key = null
                }]
            }]
        }));
        var appBuilder = new ApplicationBuilder(services.BuildServiceProvider());

        var exception = Assert.Throws<InvalidOperationException>(() => appBuilder.UseHsSqlAgent());
        Assert.Contains("requires an explicit Key", exception.Message);

        await using var verify = new AdminContext(adminDbOptions);
        Assert.Equal(0, await verify.DbManagement.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await verify.McpAccessKeys.CountAsync(TestContext.Current.CancellationToken));
    }
}
