using Admin.Service.Data;
using HsSqlAgent.Server.Test.Data;
using HsSqlAgent.Server.Extensions;
using HsSqlAgent.Server.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HsSqlAgent.Server.Test.Extensions;

public class HostModeInitializationTests
{
    [Fact]
    public void InitializeHsSqlAgent_AdminStoreOnly_DoesNotCreateBuiltInIdentitySchema()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hs-sql-agent-host-{Guid.NewGuid():N}.db");
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddHsSqlAgentCore(CreateOptions(databasePath))
                .AddHsSqlAgentAdminStore();

            using var provider = services.BuildServiceProvider();
            var app = new ApplicationBuilder(provider);

            app.InitializeHsSqlAgent();

            using var scope = provider.CreateScope();
            var adminDb = scope.ServiceProvider.GetRequiredService<AdminContext>();
            Assert.True(adminDb.Database.CanConnect());
            Assert.False(TableExists(adminDb, "Roles"));
            Assert.False(TableExists(adminDb, "Members"));
            Assert.False(TableExists(adminDb, "PermissionActions"));
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public void InitializeHsSqlAgent_V2PendingApprovalBlocksAutomaticV3Migration()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"hs-agent-v3-drain-{Guid.NewGuid():N}.db");
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            var options = CreateOptions(dbPath);
            options.JwtSecretKey = "test-jwt-key-that-is-at-least-32-bytes";
            services.AddHsSqlAgent(options);
            using var provider = services.BuildServiceProvider();
            using (var scope = provider.CreateScope())
            {
                var auth = scope.ServiceProvider.GetRequiredService<Auth.Service.Data.AuthContext>();
                auth.Database.Migrate();
                var admin = scope.ServiceProvider.GetRequiredService<AdminContext>();
                admin.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>()
                    .Migrate("20260905130453_AddDurableDmlApprovals");
                var oldApproval = new Admin.Service.Data.Entites.DmlApprovalRequestState
                {
                    RequestId = "v2-upgrade-pending",
                    ApprovalFingerprint = new string('A', 64),
                    EvidenceFingerprint = new string('B', 64),
                    Status = "Pending",
                    ProtectedExecutionPayload = "opaque-v2",
                    RequesterIdentity = "operator",
                    TargetIdentity = "db-management:1",
                    DatabaseProvider = "Sqlite",
                    DatabaseIdentity = "test",
                    AccessKeyId = 1,
                    DbManagementId = 1,
                    RequiredToolName = "execute_dml_sql",
                    StatementCount = 1,
                    TotalAffectedRows = 1,
                    CreatedAt = DateTime.UtcNow.AddMinutes(-1),
                    UpdatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(5)
                };
                LegacyDmlApprovalMigrationFixture.Insert(admin, oldApproval);
            }

            var ex = Assert.Throws<InvalidOperationException>(
                () => new ApplicationBuilder(provider).InitializeHsSqlAgent());
            Assert.Contains("fresh Admin Store", ex.Message);
            Assert.DoesNotContain("v2-upgrade-pending", ex.Message);

            using var verify = provider.CreateScope();
            var adminDb = verify.ServiceProvider.GetRequiredService<AdminContext>();
            Assert.Contains("20261022190000_V3ApprovalDrainGate",
                adminDb.Database.GetPendingMigrations());
            var connection = adminDb.Database.GetDbConnection();
            connection.Open();
            using var statusCommand = connection.CreateCommand();
            statusCommand.CommandText =
                "SELECT \"Status\" FROM \"DmlApprovalRequests\" WHERE \"RequestId\" = 'v2-upgrade-pending'";
            Assert.Equal("Pending", statusCommand.ExecuteScalar());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void InitializeHsSqlAgent_HostModeFailsClosedWhenLegacyUsersStillNeedMigration()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hs-sql-agent-legacy-{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE SuperUsers (
                        Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        Username TEXT NOT NULL,
                        Mail TEXT NOT NULL,
                        PasswordHash TEXT NOT NULL
                    );
                    INSERT INTO SuperUsers (Username, Mail, PasswordHash)
                    VALUES ('legacy-admin', 'legacy@example.test', 'hash');
                    """;
                command.ExecuteNonQuery();
            }

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddHsSqlAgentCore(CreateOptions(databasePath))
                .AddHsSqlAgentAdminStore();

            using var provider = services.BuildServiceProvider();
            var app = new ApplicationBuilder(provider);

            var exception = Assert.Throws<InvalidOperationException>(() => app.InitializeHsSqlAgent());
            Assert.Contains("fresh Admin Store", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    private static HsSqlAgentServiceOptions CreateOptions(string databasePath) => V3TestEncryption.Prepare(new()
    {
        AdminDatabaseProvider = "Sqlite",
        AdminConnectionString = $"Data Source={databasePath};Pooling=False",
        HmacSecretKey = "test-hmac-key-that-is-at-least-32-bytes"
    });

    private static bool TableExists(AdminContext adminDb, string tableName)
    {
        var connection = adminDb.Database.GetDbConnection();
        var closeConnection = connection.State != System.Data.ConnectionState.Open;
        if (closeConnection) connection.Open();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$name";
            parameter.Value = tableName;
            command.Parameters.Add(parameter);
            return Convert.ToInt64(command.ExecuteScalar()) > 0;
        }
        finally
        {
            if (closeConnection) connection.Close();
        }
    }
}
