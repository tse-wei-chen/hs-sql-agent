using Admin.Service.Data;
using HsSqlAgent.Server.Extensions;
using HsSqlAgent.Server.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
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
    public void InitializeHsSqlAgent_HostModeFailsClosedWhenLegacyUsersStillNeedMigration()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hs-sql-agent-legacy-{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}"))
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
            Assert.Contains("legacy HsSqlAgent users", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    private static HsSqlAgentServiceOptions CreateOptions(string databasePath) => new()
    {
        AdminDatabaseProvider = "Sqlite",
        AdminConnectionString = $"Data Source={databasePath}",
        HmacSecretKey = "test-hmac-key-that-is-at-least-32-bytes"
    };

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
